using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CloudFrame.Providers.OneDrive
{
    /// <summary>
    /// Manages MSAL.NET authentication for a single personal Microsoft account.
    ///
    /// When an Edge profile folder name is supplied, interactive login opens
    /// Edge in that specific profile via --profile-directory, so the user
    /// signs in with the correct Microsoft account.
    /// </summary>
    public sealed class MsalAuthManager
    {
        private static readonly string[] s_scopes =
        [
            "Files.Read",
            "offline_access"
        ];

        // Replace with your Azure AD app registration Client ID.
        // See docs/AzureAppRegistration.md for setup instructions.
        private const string ClientId = "ba60e40c-701f-4502-a808-c130d60eb51e";
        private const string Authority = "https://login.microsoftonline.com/consumers";

        private readonly IPublicClientApplication _msal;
        private readonly string _accountId;
        private readonly string? _edgeProfileFolder;   // e.g. "Default" or "Profile 1"
        private AuthenticationResult? _lastResult;

        /// <param name="accountId">CloudFrame AccountConfig.AccountId (stable GUID).</param>
        /// <param name="edgeProfileFolder">
        /// Edge profile folder name to use for interactive login.
        /// Null = let MSAL use the system default browser.
        /// </param>
        /// <param name="cacheDirectory">
        /// Directory for the DPAPI-encrypted token cache.
        /// Null = %LOCALAPPDATA%\CloudFrame.
        /// </param>
        public MsalAuthManager(
            string accountId,
            string? edgeProfileFolder = null,
            string? cacheDirectory = null)
        {
            _accountId = accountId;
            _edgeProfileFolder = edgeProfileFolder;

            _msal = PublicClientApplicationBuilder
                .Create(ClientId)
                .WithAuthority(Authority)
                .WithDefaultRedirectUri()
                .Build();

            _ = RegisterCacheAsync(cacheDirectory);
        }

        /// <summary>Current access token. Refreshed silently by MSAL as needed.</summary>
        public string? AccessToken => _lastResult?.AccessToken;

        /// <summary>
        /// Tries to acquire a token silently (no browser, no UI).
        /// Returns false when interactive login is required.
        /// </summary>
        public async Task<bool> EnsureAuthenticatedAsync(CancellationToken ct = default)
        {
            try
            {
                var accounts = await _msal.GetAccountsAsync().ConfigureAwait(false);
                var account = accounts.FirstOrDefault();
                if (account is null) return false;

                _lastResult = await _msal
                    .AcquireTokenSilent(s_scopes, account)
                    .ExecuteAsync(ct)
                    .ConfigureAwait(false);

                return true;
            }
            catch (MsalUiRequiredException)
            {
                return false;
            }
            catch (MsalServiceException ex)
                when (ex.ErrorCode is "request_timeout" or "temporarily_unavailable")
            {
                return false;
            }
        }

        /// <summary>
        /// Opens an interactive sign-in flow in Edge, using the configured
        /// profile if one was specified. Must be called from the UI thread.
        /// </summary>
        public async Task<bool> AcquireTokenInteractiveAsync(CancellationToken ct = default)
        {
            try
            {
                var builder = _msal
                    .AcquireTokenInteractive(s_scopes)
                    .WithPrompt(Prompt.SelectAccount);

                // If an Edge profile is configured, open Edge in that profile
                // instead of the system default browser.
                string? edgePath = EdgeProfileDetector.GetEdgeExecutablePath();
                if (edgePath is not null && _edgeProfileFolder is not null)
                {
                    builder = builder.WithSystemWebViewOptions(
                        new SystemWebViewOptions
                        {
                            // MSAL opens this URL in the browser.
                            // We override the browser executable and pass
                            // --profile-directory so Edge uses the right profile.
                            OpenBrowserAsync = (Uri url) =>
                            {
                                var args = $"--profile-directory=\"{_edgeProfileFolder}\" \"{url}\"";
                                System.Diagnostics.Process.Start(
                                    new System.Diagnostics.ProcessStartInfo
                                    {
                                        FileName = edgePath,
                                        Arguments = args,
                                        UseShellExecute = false
                                    });
                                return Task.CompletedTask;
                            }
                        });
                }

                _lastResult = await builder.ExecuteAsync(ct).ConfigureAwait(false);
                return true;
            }
            catch (MsalException)
            {
                return false;
            }
        }

        /// <summary>
        /// Removes all cached tokens for this account.
        /// </summary>
        public async Task SignOutAsync()
        {
            var accounts = await _msal.GetAccountsAsync().ConfigureAwait(false);
            foreach (var account in accounts)
                await _msal.RemoveAsync(account).ConfigureAwait(false);
            _lastResult = null;
        }

        // ── Token cache ────────────────────────────────────────────────────────

        private async Task RegisterCacheAsync(string? cacheDirectory)
        {
            var dir = cacheDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CloudFrame");

            Directory.CreateDirectory(dir);

            try
            {
                var storageProps = new StorageCreationPropertiesBuilder(
                        $"msal_{_accountId}.cache", dir)
                    .Build();

                var helper = await MsalCacheHelper
                    .CreateAsync(storageProps)
                    .ConfigureAwait(false);

                helper.RegisterCache(_msal.UserTokenCache);
            }
            catch
            {
                // If cache registration fails (e.g. DPAPI unavailable),
                // fall back to in-memory only — user will need to re-authenticate
                // on next launch, but nothing crashes.
            }
        }
    }
}