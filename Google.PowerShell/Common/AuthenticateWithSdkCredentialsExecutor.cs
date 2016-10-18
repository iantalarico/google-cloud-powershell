﻿/*
Copyright 2016 Google Inc

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Http;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Google.PowerShell.Common
{
    /// <summary>
    /// OAuth 2.0 credential for accessing protected resources using an access token.
    /// This class will get the access token from "gcloud auth print-access-token".
    /// </summary>
    public class AuthenticateWithSdkCredentialsExecutor : ICredential, IHttpExecuteInterceptor, IHttpUnsuccessfulResponseHandler
    {
        /// <summary>
        /// This is a cache of the last call to "gcloud auth print-access-token".
        /// If the current user hasn't changed, and the token hasn't expired, we reuse it
        /// rather than launching gcloud again.
        /// </summary>
        private static ActiveUserToken s_token;
        private static readonly SemaphoreSlim s_tokenLock = new SemaphoreSlim(1);

        /// <summary>
        /// Returns the authorization code flow. 
        /// This is used to revoke token (in RevokeTokenAsync)
        /// and intercept request with the access token (in InterceptAsync).
        /// </summary>
        public IAuthorizationCodeFlow Flow { get; } = new GoogleAuthorizationCodeFlow(
                new GoogleAuthorizationCodeFlow.Initializer()
                {
                    ClientSecrets = new ClientSecrets()
                    {
                        ClientId = "clientId",
                        ClientSecret = "clientSecrets"
                    }
                });

        #region IHttpExecuteInterceptor

        /// <summary>
        /// Default implementation is to try to refresh the access token if there is no access token or if we are 1 
        /// minute away from expiration. If token server is unavailable, it will try to use the access token even if 
        /// has expired. If successful, it will call <see cref="IAccessMethod.Intercept"/>.
        /// </summary>
        public async Task InterceptAsync(HttpRequestMessage request, CancellationToken taskCancellationToken)
        {
            Task<string> getAccessTokenTask = GetAccessTokenForRequestAsync(request.RequestUri.ToString(), taskCancellationToken);
            string accessToken = await getAccessTokenTask.ConfigureAwait(false);
            Flow.AccessMethod.Intercept(request, accessToken);
        }

        #endregion

        #region IHttpUnsuccessfulResponseHandler

        public async Task<bool> HandleResponseAsync(HandleUnsuccessfulResponseArgs args)
        {
            if (args.Response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return await RefreshTokenAsync(args.CancellationToken).ConfigureAwait(false);
            }

            return false;
        }

        #endregion

        #region IConfigurableHttpClientInitializer

        public void Initialize(ConfigurableHttpClient httpClient)
        {
            httpClient.MessageHandler.AddExecuteInterceptor(this);
            httpClient.MessageHandler.AddUnsuccessfulResponseHandler(this);
        }

        #endregion

        #region ITokenAccess implementation

        public virtual async Task<string> GetAccessTokenForRequestAsync(string authUri = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (s_token == null)
            {
                await RefreshTokenAsync(cancellationToken).ConfigureAwait(false);
                return s_token.AccessToken;
            }

            if (s_token.IsExpiredOrInvalid())
            {
                if (!await RefreshTokenAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("The access token has expired but we can't refresh it");
                }
            }

            return s_token.AccessToken;
        }

        #endregion

        /// <summary>
        /// Refreshes the token by calling to GCloudWrapper.GetAccessToken
        /// </summary>
        public async Task<bool> RefreshTokenAsync(CancellationToken taskCancellationToken)
        {
            await s_tokenLock.WaitAsync();
            try
            {
                s_token = await GCloudWrapper.GetAccessToken(taskCancellationToken);
                return true;
            }
            finally
            {
                s_tokenLock.Release();
            }
        }

        /// <summary>
        /// Asynchronously revokes the token by calling
        /// <see cref="Google.Apis.Auth.OAuth2.Flows.IAuthorizationCodeFlow.RevokeTokenAsync"/>.
        /// </summary>
        /// <param name="taskCancellationToken">Cancellation token to cancel an operation.</param>
        /// <returns><c>true</c> if the token was revoked successfully.</returns>
        public async Task<bool> RevokeTokenAsync(CancellationToken taskCancellationToken)
        {
            if (s_token == null)
            {
                return false;
            }

            await Flow.RevokeTokenAsync("userId", s_token.AccessToken, taskCancellationToken).ConfigureAwait(false);
            // We don't set the token to null, cause we want that the next request (without reauthorizing) will fail).
            return true;
        }
    }
}
