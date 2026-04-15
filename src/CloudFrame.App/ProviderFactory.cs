using System.Collections.Generic;
using System.Net.Http;
using CloudFrame.Core.Cloud;
using CloudFrame.Core.Config;
using CloudFrame.Providers.OneDrive;

namespace CloudFrame.App
{
    internal static class ProviderFactory
    {
        // Key used in AccountConfig.ProviderSettings to store the chosen
        // Edge profile folder name (e.g. "Default" or "Profile 1").
        public const string EdgeProfileKey = "EdgeProfileFolder";

        public static List<ICloudProvider> Build(AppSettings settings, HttpClient http)
        {
            var result = new List<ICloudProvider>();

            foreach (var account in settings.Accounts)
            {
                if (!account.IsEnabled) continue;

                account.ProviderSettings.TryGetValue(EdgeProfileKey, out string? edgeProfile);

                ICloudProvider? provider = account.ProviderType switch
                {
                    "OneDrive" => new OneDriveProvider(
                        account.AccountId,
                        account.DisplayName,
                        http,
                        edgeProfileFolder: edgeProfile),

                    _ => null
                };

                if (provider is not null)
                    result.Add(provider);
            }

            return result;
        }
    }
}