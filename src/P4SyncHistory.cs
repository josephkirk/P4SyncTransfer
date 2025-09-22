using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace P4Sync
{
    /// <summary>
    /// Class for managing sync operation history stored in queryable JSON format, with separate files per day
    /// </summary>
    public class P4SyncHistory
    {
        private readonly string historyDirectory;

        public P4SyncHistory(string directory)
        {
            historyDirectory = directory;
            Directory.CreateDirectory(historyDirectory);
        }

        /// <summary>
        /// Logs a sync operation history to the JSON file for the corresponding day
        /// </summary>
        /// <param name="syncHistory">The sync history to log</param>
        public void LogSync(SyncHistory syncHistory)
        {
            if (syncHistory.Syncs.Count == 0) return;

            var date = syncHistory.Syncs.First().SyncTime.Date;
            var filePath = GetFilePathForDate(date);
            var histories = LoadHistoriesForDate(date);
            histories.Add(syncHistory);
            SaveHistoriesForDate(histories, date);
        }

        /// <summary>
        /// Loads all sync histories from all date files
        /// </summary>
        /// <returns>List of all sync histories</returns>
        public List<SyncHistory> LoadAllHistories()
        {
            var allHistories = new List<SyncHistory>();
            var files = Directory.GetFiles(historyDirectory, "sync_history_*.json");
            foreach (var file in files)
            {
                var json = File.ReadAllText(file);
                var histories = JsonSerializer.Deserialize<List<SyncHistory>>(json) ?? new List<SyncHistory>();
                allHistories.AddRange(histories);
            }
            return allHistories;
        }

        /// <summary>
        /// Loads sync histories for a specific date
        /// </summary>
        /// <param name="date">The date to load histories for</param>
        /// <returns>List of sync histories for the date</returns>
        public List<SyncHistory> LoadHistoriesForDate(DateTime date)
        {
            var filePath = GetFilePathForDate(date);
            if (!File.Exists(filePath))
                return new List<SyncHistory>();

            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<List<SyncHistory>>(json) ?? new List<SyncHistory>();
        }

        /// <summary>
        /// Saves the list of sync histories for a specific date to JSON
        /// </summary>
        /// <param name="histories">List of sync histories to save</param>
        /// <param name="date">The date for the file</param>
        private void SaveHistoriesForDate(List<SyncHistory> histories, DateTime date)
        {
            var filePath = GetFilePathForDate(date);
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(histories, options);
            File.WriteAllText(filePath, json);
        }

        /// <summary>
        /// Gets the file path for a specific date
        /// </summary>
        /// <param name="date">The date</param>
        /// <returns>File path</returns>
        private string GetFilePathForDate(DateTime date)
        {
            return Path.Combine(historyDirectory, $"sync_history_{date:yyyy-MM-dd}.json");
        }

        /// <summary>
        /// Queries transfers across all histories based on a predicate
        /// </summary>
        /// <param name="predicate">Predicate to filter transfers</param>
        /// <returns>Enumerable of matching transfers</returns>
        public IEnumerable<P4SyncedTransfer> QueryTransfers(Func<P4SyncedTransfer, bool> predicate)
        {
            var histories = LoadAllHistories();
            return histories.SelectMany(h => h.Syncs).SelectMany(s => s.Transfers).Where(predicate);
        }

        /// <summary>
        /// Queries syncs based on a predicate
        /// </summary>
        /// <param name="predicate">Predicate to filter syncs</param>
        /// <returns>Enumerable of matching syncs</returns>
        public IEnumerable<P4SyncedTransfers> QuerySyncs(Func<P4SyncedTransfers, bool> predicate)
        {
            var histories = LoadAllHistories();
            return histories.SelectMany(h => h.Syncs).Where(predicate);
        }

        /// <summary>
        /// Gets the latest sync for a specific profile
        /// </summary>
        /// <param name="profileName">Name of the profile</param>
        /// <returns>The latest sync transfers or null if not found</returns>
        public P4SyncedTransfers? GetLatestSync(string profileName)
        {
            var histories = LoadAllHistories();
            var profileHistory = histories.FirstOrDefault(h => h.Profile?.Name == profileName);
            return profileHistory?.Syncs.OrderByDescending(s => s.SyncTime).FirstOrDefault();
        }
    }
}