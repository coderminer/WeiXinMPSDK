﻿/*----------------------------------------------------------------
    Copyright (C) 2016 Senparc

    文件名：AccessTokenContainer.cs
    文件功能描述：通用接口AccessToken容器，用于自动管理AccessToken，如果过期会重新获取


    创建标识：Senparc - 20150313

    修改标识：Senparc - 20150313
    修改描述：整理接口

    修改标识：Senparc - 20160206
    修改描述：将public object Lock更改为internal object Lock

    修改标识：Senparc - 20160312
    修改描述：1、升级Container，继承BaseContainer
              2、使用新的AccessToken有效期机制

    修改标识：Senparc - 20160318
    修改描述：v3.3.4 使用FlushCache.CreateInstance使注册过程立即生效
    
    修改标识：Senparc - 20160717
    修改描述：v3.3.8 添加注册过程中的Name参数
    
    修改标识：Senparc - 20160803
    修改描述：v4.1.2 使用ApiUtility.GetExpireTime()方法处理过期
 
    修改标识：Senparc - 20160804
    修改描述：v4.1.3 增加TryGetTokenAsync，GetTokenAsync，GetTokenResultAsync的异步方法
    
    修改标识：Senparc - 20160813
    修改描述：v4.1.5 添加TryReRegister()方法，处理分布式缓存重启（丢失）的情况

    修改标识：Senparc - 20160813
    修改描述：v4.1.6 完善GetToken()方法
    
    修改标识：Senparc - 20160813
    修改描述：v4.1.8 修改命名空间为Senparc.Weixin.QY.Containers
----------------------------------------------------------------*/

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Senparc.Weixin.CacheUtility;
using Senparc.Weixin.Containers;
using Senparc.Weixin.Exceptions;
using Senparc.Weixin.QY.CommonAPIs;
using Senparc.Weixin.QY.Entities;
using Senparc.Weixin.QY.Exceptions;
using Senparc.Weixin.Utilities.WeixinUtility;

namespace Senparc.Weixin.QY.Containers
{
    [Serializable]
    public class AccessTokenBag : BaseContainerBag
    {
        /// <summary>
        /// CorpId
        /// </summary>
        public string CorpId
        {
            get { return _corpId; }
            set { base.SetContainerProperty(ref _corpId, value); }
        }
        /// <summary>
        /// CorpSecret
        /// </summary>
        public string CorpSecret
        {
            get { return _corpSecret; }
            set { base.SetContainerProperty(ref _corpSecret, value); }
        }
        /// <summary>
        /// 过期时间
        /// </summary>
        public DateTime ExpireTime
        {
            get { return _expireTime; }
            set { base.SetContainerProperty(ref _expireTime, value); }
        }
        /// <summary>
        /// AccessTokenResult
        /// </summary>
        public AccessTokenResult AccessTokenResult
        {
            get { return _accessTokenResult; }
            set { base.SetContainerProperty(ref _accessTokenResult, value); }
        }

        /// <summary>
        /// 只针对这个CorpId的锁
        /// </summary>
        internal object Lock = new object();

        private string _corpId;
        private string _corpSecret;
        private DateTime _expireTime;
        private AccessTokenResult _accessTokenResult;
    }

    /// <summary>
    /// 通用接口AccessToken容器，用于自动管理AccessToken，如果过期会重新获取
    /// </summary>
    public class AccessTokenContainer : BaseContainer<AccessTokenBag>
    {
        private const string UN_REGISTER_ALERT = "此CorpId尚未注册，AccessTokenContainer.Register完成注册（全局执行一次即可）！";

        /// <summary>
        /// 注册应用凭证信息，此操作只是注册，不会马上获取Token，并将清空之前的Token。
        /// 执行此注册过程，会连带注册ProviderTokenContainer。
        /// </summary>
        /// <param name="corpId"></param>
        /// <param name="corpSecret"></param>
        /// <param name="name">标记AccessToken名称（如微信公众号名称），帮助管理员识别</param>
        /// 此接口无异步方法
        private static string BuildingKey(string corpId, string corpSecret)
        {
            return corpId + corpSecret;
        }

        public static void Register(string corpId, string corpSecret, string name = null)
        {
            //记录注册信息，RegisterFunc委托内的过程会在缓存丢失之后自动重试
            RegisterFunc = () =>
            {
                using (FlushCache.CreateInstance())
                {
                    var bag = new AccessTokenBag()
                    {
                        Name = name,
                        CorpId = corpId,
                        CorpSecret = corpSecret,
                        ExpireTime = DateTime.MinValue,
                        AccessTokenResult = new AccessTokenResult()
                    };
                    Update(BuildingKey(corpId,corpSecret), bag);
                    return bag;
                }
            };
            RegisterFunc();

            JsApiTicketContainer.Register(corpId, corpSecret);//连带注册JsApiTicketContainer

            ProviderTokenContainer.Register(corpId, corpSecret);//连带注册ProviderTokenContainer
        }

        #region 同步方法


        /// <summary>
        /// 使用完整的应用凭证获取Token，如果不存在将自动注册
        /// </summary>
        /// <param name="corpId"></param>
        /// <param name="corpSecret"></param>
        /// <param name="getNewToken"></param>
        /// <returns></returns>
        public static string TryGetToken(string corpId, string corpSecret, bool getNewToken = false)
        {
            if (!CheckRegistered(BuildingKey(corpId, corpSecret)) || getNewToken)
            {
                Register(corpId, corpSecret);
            }
            return GetToken(corpId, corpSecret, getNewToken);
        }

        /// <summary>
        /// 获取可用Token
        /// </summary>
        /// <param name="corpId"></param>
        /// <param name="getNewToken">是否强制重新获取新的Token</param>
        /// <returns></returns>
        public static string GetToken(string corpId, string corpSecret, bool getNewToken = false)
        {
            return GetTokenResult(corpId, corpSecret,getNewToken).access_token;
        }

        /// <summary>
        /// 获取可用Token
        /// </summary>
        /// <param name="corpId"></param>
        /// <param name="getNewToken">是否强制重新获取新的Token</param>
        /// <returns></returns>
        public static AccessTokenResult GetTokenResult(string corpId,string corpSecret,bool getNewToken = false)
        {
            if (!CheckRegistered(BuildingKey(corpId, corpSecret)))
            {
                throw new WeixinQyException(UN_REGISTER_ALERT);
            }

            var accessTokenBag = TryGetItem(BuildingKey(corpId, corpSecret));
            lock (accessTokenBag.Lock)
            {
                if (getNewToken || accessTokenBag.ExpireTime <= DateTime.Now)
                {
                    //已过期，重新获取
                    accessTokenBag.AccessTokenResult = CommonApi.GetToken(accessTokenBag.CorpId,
                        accessTokenBag.CorpSecret);
                    accessTokenBag.ExpireTime = ApiUtility.GetExpireTime(accessTokenBag.AccessTokenResult.expires_in);
                }
            }
            return accessTokenBag.AccessTokenResult;
        }

        ///// <summary>
        ///// 检查是否已经注册
        ///// </summary>
        ///// <param name="corpId"></param>
        ///// <returns></returns>
        ///// 此接口无异步方法
        //public new static bool CheckRegistered(string corpId)
        //{
        //    return Cache.CheckExisted(corpId);
        //}

        #endregion

        #region 异步方法
        /// <summary>
        /// 【异步方法】使用完整的应用凭证获取Token，如果不存在将自动注册
        /// </summary>
        /// <param name="corpId"></param>
        /// <param name="corpSecret"></param>
        /// <param name="getNewToken"></param>
        /// <returns></returns>
        public static async Task<string> TryGetTokenAsync(string corpId, string corpSecret, bool getNewToken = false)
        {
            if (!CheckRegistered(BuildingKey(corpId, corpSecret)) || getNewToken)
            {
                Register(corpId, corpSecret);
            }
            return await GetTokenAsync(corpId, corpSecret, getNewToken);
        }

        /// <summary>
        /// 【异步方法】获取可用Token
        /// </summary>
        /// <param name="corpId"></param>
        /// <param name="getNewToken">是否强制重新获取新的Token</param>
        /// <returns></returns>
        public static async Task<string> GetTokenAsync(string corpId,string corpSecret, bool getNewToken = false)
        {
            var result = await GetTokenResultAsync(corpId, corpSecret, getNewToken);
            return result.access_token;
        }

        /// <summary>
        /// 【异步方法】获取可用Token
        /// </summary>
        /// <param name="corpId"></param>
        /// <param name="getNewToken">是否强制重新获取新的Token</param>
        /// <returns></returns>
        public static async Task<AccessTokenResult> GetTokenResultAsync(string corpId,string corpSecret, bool getNewToken = false)
        {
            if (!CheckRegistered(BuildingKey(corpId, corpSecret)))
            {
                throw new WeixinQyException(UN_REGISTER_ALERT);
            }

            var accessTokenBag = TryGetItem(BuildingKey(corpId, corpSecret));
            // lock (accessTokenBag.Lock)
            {
                if (getNewToken || accessTokenBag.ExpireTime <= DateTime.Now)
                {
                    //已过期，重新获取
                    var accessTokenResult = await CommonApi.GetTokenAsync(accessTokenBag.CorpId,
                        accessTokenBag.CorpSecret);
                    //accessTokenBag.AccessTokenResult = CommonApi.GetToken(accessTokenBag.CorpId,
                    //    accessTokenBag.CorpSecret);
                    accessTokenBag.AccessTokenResult = accessTokenResult;
                    accessTokenBag.ExpireTime = ApiUtility.GetExpireTime(accessTokenBag.AccessTokenResult.expires_in);
                }
            }
            return accessTokenBag.AccessTokenResult;
        }
        #endregion
    }
}
