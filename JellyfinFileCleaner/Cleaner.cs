﻿namespace EmbyFileCleaner
{
    using Emby.ApiClient;
    using Emby.ApiClient.Cryptography;
    using Emby.ApiClient.Model;
    using MediaBrowser.Model.Dto;
    using MediaBrowser.Model.Entities;
    using MediaBrowser.Model.Logging;
    using MediaBrowser.Model.Querying;
    using Model.Json;
    using System;
    using System.Linq;
    using System.Reflection;
    using System.Threading.Tasks;

    public class Cleaner
    {
        private readonly Config config;
        private readonly IApiClient apiClient;
        private static readonly log4net.ILog Logger = log4net.LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);


        public Cleaner(Config config)
        {
            this.config = config;
            this.apiClient = GetApiClientInstanceAsync(this.config).GetAwaiter().GetResult();
        }

        public void Run()
        {
            RunAsync().GetAwaiter().GetResult();
        }

        private async Task RunAsync()
        {
            string userId = await GetUserIdByUsername(this.config.ConnectionInfo.Username);
            var playedItems = (await GetItems(userId))
                .Where(item =>
                {
                    var lastPlayedDate = item.UserData?.LastPlayedDate;
                    return lastPlayedDate != null && lastPlayedDate < DateTime.Now.AddDays(-this.config.RemoveOlderThanDays);
                })
            .OrderBy(item => GetItemNameFormattedByType(item));
            var validItems = playedItems.Where(IsNotIgnored);
            int pickedCount = playedItems.Count();
            int deletedCount = 0;
            int failedCount = 0;

            foreach (var item in validItems)
            {
                if (this.config.IsTest)
                {
                    Logger.Info($"Picked - {GetItemNameFormattedByType(item)}");
                }
                else if (await TryDeleteAsync(item))
                {
                    Logger.Info($"Deleted - {GetItemNameFormattedByType(item)}");
                    deletedCount++;
                }
                else
                {
                    failedCount++;
                }
            }

            SaveSummary(pickedCount, deletedCount, pickedCount - validItems.Count(), failedCount);
        }

        private void SaveSummary(int pickedCount, int deletedCount, int ignoredCount, int failedCount)
        {
            Logger.Info($"{Environment.NewLine}==================={Environment.NewLine}" +
                $"Picked Count - {pickedCount}{Environment.NewLine}" +
                $"Deleted Count - {deletedCount}{Environment.NewLine}" +
                $"Ignored Count - {ignoredCount}{Environment.NewLine}" +
                $"Failed Count - {failedCount}{Environment.NewLine}" +
                $"===================");
        }

        private async Task<bool> TryDeleteAsync(BaseItemDto item)
        {
            try
            {
                if (item.CanDelete ?? true)
                {
                    await this.apiClient.DeleteItemAsync(item.Id);
                    return true;
                }

                throw new InvalidOperationException("Item marked not to be deleted.");
            }
            catch (Exception e)
            {
                Logger.Error($"Could not delete {GetItemNameFormattedByType(item)}: {e.Message}", e);
                return false;
            }
        }

        private string GetItemNameByType(BaseItemDto item)
        {
            if (Enum.TryParse(item.Type, out ItemType itemType))
            {
                switch (itemType)
                {
                    case ItemType.Episode:
                        return item.SeriesName;
                    case ItemType.Movie:
                        return item.Name;
                    default:
                        throw new ArgumentOutOfRangeException(item.Type);
                }
            }

            throw new InvalidOperationException();
        }

        private string GetItemNameFormattedByType(BaseItemDto item)
        {
            if (Enum.TryParse(item.Type, out ItemType itemType))
            {
                switch (itemType)
                {
                    case ItemType.Episode:
                        return $"{item.SeriesName} - {item.Name}";
                    case ItemType.Movie:
                        return $"{item.Name} - {item.ProductionYear}";
                    default:
                        throw new ArgumentOutOfRangeException(item.Type);
                }
            }

            throw new InvalidOperationException();
        }

        private bool IsNotIgnored(BaseItemDto item)
        {
            bool isIgnoredListContains = this.config.IgnoreListContains.Any(name => GetItemNameByType(item).ToLower().Contains(name.ToLower()));
            bool isIgnoredEquals = this.config.IgnoreListEquals.Any(name => GetItemNameByType(item).ToLower() == name.ToLower());
            bool ignored = isIgnoredListContains || isIgnoredEquals;

            if (ignored && this.config.PrintIgnored)
            {
                Logger.Info($"Ignored - {GetItemNameFormattedByType(item)}");
            }

            return ignored == false;
        }

        private async Task<string> GetUserIdByUsername(string username)
        {
            var users = await this.apiClient.GetUsersAsync(new UserQuery());

            var user = users.SingleOrDefault(u => u.Name.ToLower() == username.ToLower());
            if (user == null)
            {
                string message = $"Could not find a user for name {username}";
                Logger.Error(message);
                throw new ArgumentException(message);
            }

            return user.Id;
        }

        private async Task<BaseItemDto[]> GetItems(string userId)
        {
            var items = await this.apiClient.GetItemsAsync(new ItemQuery
            {
                SortBy = new[] { ItemSortBy.DatePlayed },
                SortOrder = SortOrder.Ascending,
                IncludeItemTypes = this.config.IncludeItemTypes.Select(t => t.ToString()).ToArray(),
                IsPlayed = true,
                Recursive = true,
                UserId = userId,
                Fields = new ItemFields[] { }
            });

            return items.Items;
        }

        private async Task<IApiClient> GetApiClientInstanceAsync(Config configLocal)
        {
            var logger = new NullLogger();
            var cryptoProvider = new CryptographyProvider();

            IApiClient client = null;

            if (string.IsNullOrEmpty(configLocal.ConnectionInfo.ApiKey) == false)
            {
                Logger.Warn("If api key is granted manually (thru advanced -> security in admin panel), it will only work then in test mode. Use login/password or api key generated in user context to be able to delete items.");
                client = new ApiClient(logger, configLocal.ConnectionInfo.Endpoint, configLocal.ConnectionInfo.ApiKey, cryptoProvider);
            }
            else
            {
                var device = new Device { DeviceId = string.Empty, DeviceName = string.Empty };
                client = new ApiClient(logger, configLocal.ConnectionInfo.Endpoint, "EmbyFileCleaner", device, Program.GetAssemblyVersion(), cryptoProvider);
                string passwordMd5 = ConnectService.GetConnectPasswordMd5(configLocal.ConnectionInfo.Password ?? string.Empty, cryptoProvider);
                var authResult = await client.AuthenticateUserAsync(configLocal.ConnectionInfo.Username, passwordMd5);
                client = new ApiClient(logger, configLocal.ConnectionInfo.Endpoint, authResult.AccessToken, cryptoProvider);
            }

            return client;
        }
    }
}
