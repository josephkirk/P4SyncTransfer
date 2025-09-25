using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace P4Sync
{
    /// <summary>
    /// Class for managing sync operation history stored in queryable JSON format, with separate files per day
    /// </summary>
    public class P4SyncHistory
    {
        private readonly string historyDirectory;
        private readonly bool enableFileWriting;

        public P4SyncHistory(string directory, bool enableFileWriting = true)
        {
            historyDirectory = directory;
            this.enableFileWriting = enableFileWriting;
            if (enableFileWriting)
            {
                Directory.CreateDirectory(historyDirectory);
            }
        }

        /// <summary>
        /// Logs a sync operation history to the JSON file for the corresponding day
        /// </summary>
        /// <param name="syncHistory">The sync history to log</param>
        public void LogSync(SyncHistory syncHistory)
        {
            if (!enableFileWriting || syncHistory.Syncs.Count == 0) return;

            syncHistory.ProfileId = ComputeProfileId(syncHistory.Profile);

            var date = syncHistory.Syncs.First().SyncTime.Date;
            var filePath = GetFilePathForDate(date);
            var histories = LoadHistoriesForDate(date);

            var existingHistory = histories.FirstOrDefault(h => h.ProfileId == syncHistory.ProfileId);
            if (existingHistory != null)
            {
                existingHistory.Syncs.AddRange(syncHistory.Syncs);
            }
            else
            {
                histories.Add(syncHistory);
            }

            SaveHistoriesForDate(histories, date);
        }

        /// <summary>
        /// Loads all sync histories from all date files
        /// </summary>
        /// <returns>List of all sync histories</returns>
        public List<SyncHistory> LoadAllHistories()
        {
            var allHistories = new List<SyncHistory>();
            if (!Directory.Exists(historyDirectory))
                return allHistories;

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
        /// <param name="profile">The sync profile</param>
        /// <returns>The latest sync transfers or null if not found</returns>
        public P4SyncedTransfers? GetLatestSync(SyncProfile profile)
        {
            var profileId = ComputeProfileId(profile);
            var histories = LoadAllHistories();
            var profileHistory = histories.FirstOrDefault(h => h.ProfileId == profileId);
            return profileHistory?.Syncs.OrderByDescending(s => s.SyncTime).FirstOrDefault();
        }

        /// <summary>
        /// Logs a single transfer to the latest unfinished transfers (changelist=0) for the specified profile.
        /// If no unfinished transfers exist, creates new transfers to log the transfer.
        /// </summary>
        /// <param name="transfer">The transfer to log</param>
        /// <param name="profile">The sync profile</param>
        public void LogTransfer(P4SyncedTransfer transfer, SyncProfile profile)
        {
            if (!enableFileWriting) return;

            var profileId = ComputeProfileId(profile);
            var today = DateTime.Now.Date;
            var histories = LoadHistoriesForDate(today);
            
            // Find the history for this profile
            var profileHistory = histories.FirstOrDefault(h => h.ProfileId == profileId);
            
            if (profileHistory == null)
            {
                // Create a new profile history with full profile info
                profileHistory = new SyncHistory
                {
                    ProfileId = profileId,
                    Profile = profile,
                    Syncs = new List<P4SyncedTransfers>()
                };
                histories.Add(profileHistory);
            }
            else
            {
                // CRITICAL FIX: Update the profile info in case it has changed
                profileHistory.Profile = profile;
            }

            // Find the latest unfinished transfers (ChangelistNumber = 0)
            var unfinishedTransfers = profileHistory.Syncs
                .Where(s => s.ChangelistNumber == 0)
                .OrderByDescending(s => s.SyncTime)
                .FirstOrDefault();

            if (unfinishedTransfers == null)
            {
                // Create new transfers to log this transfer
                unfinishedTransfers = new P4SyncedTransfers
                {
                    SyncTime = DateTime.Now,
                    ChangelistNumber = 0,
                    Transfers = new List<P4SyncedTransfer>()
                };
                profileHistory.Syncs.Add(unfinishedTransfers);
            }

            // Add the transfer to the unfinished transfers
            unfinishedTransfers.Transfers.Add(transfer);

            // Save the updated histories (this preserves all existing syncs)
            SaveHistoriesForDate(histories, today);
        }

        /// <summary>
        /// Logs a single transfer to the latest unfinished transfers (changelist=0) for the specified SyncHistory.
        /// If no unfinished transfers exist, creates new transfers to log the transfer.
        /// </summary>
        /// <param name="transfer">The transfer to log</param>
        /// <param name="syncHistory">The sync history to update</param>
        public void LogTransfer(P4SyncedTransfer transfer, SyncHistory syncHistory)
        {
            if (!enableFileWriting || syncHistory?.Syncs == null) return;

            // Find the latest unfinished transfers (ChangelistNumber = 0)
            var unfinishedTransfers = syncHistory.Syncs
                .Where(s => s.ChangelistNumber == 0)
                .OrderByDescending(s => s.SyncTime)
                .FirstOrDefault();

            if (unfinishedTransfers == null)
            {
                // Create new transfers to log this transfer
                unfinishedTransfers = new P4SyncedTransfers
                {
                    SyncTime = DateTime.Now,
                    ChangelistNumber = 0,
                    Transfers = new List<P4SyncedTransfer>()
                };
                syncHistory.Syncs.Add(unfinishedTransfers);
            }

            // Add the transfer to the unfinished transfers
            unfinishedTransfers.Transfers.Add(transfer);

            // Determine which date file to save to based on the sync time
            var syncDate = unfinishedTransfers.SyncTime.Date;
            var histories = LoadHistoriesForDate(syncDate);
            
            // Find and update the corresponding history in the loaded histories
            var existingHistory = histories.FirstOrDefault(h => h.ProfileId == syncHistory.ProfileId);
            if (existingHistory != null)
            {
                // Find the corresponding sync in the existing history
                var existingSync = existingHistory.Syncs.FirstOrDefault(s => s.SyncTime == unfinishedTransfers.SyncTime && s.ChangelistNumber == unfinishedTransfers.ChangelistNumber);
                if (existingSync != null)
                {
                    // Update the existing sync with all transfers from the in-memory object
                    existingSync.Transfers = unfinishedTransfers.Transfers;
                }
                else
                {
                    // If the sync doesn't exist in the file, add it
                    existingHistory.Syncs.Add(unfinishedTransfers);
                }
            }
            else
            {
                // If the history doesn't exist in the file, add it
                histories.Add(syncHistory);
            }

            // Save the updated histories
            SaveHistoriesForDate(histories, syncDate);
        }

        /// <summary>
        /// Updates the ChangelistNumber of the latest unfinished P4SyncedTransfers for the specified profile.
        /// If no latest unfinished transfers are found, the operation is skipped.
        /// </summary>
        /// <param name="profile">The sync profile</param>
        /// <param name="changelistNumber">The changelist number to set</param>
        /// <returns>True if the update was successful, false if no unfinished transfers were found</returns>
        public bool UpdateLatestUnfinishedChangelistNumber(SyncProfile profile, int changelistNumber)
        {
            if (!enableFileWriting) return false;

            var profileId = ComputeProfileId(profile);
            var today = DateTime.Now.Date;
            var histories = LoadHistoriesForDate(today);
            
            // Find the history for this profile
            var profileHistory = histories.FirstOrDefault(h => h.ProfileId == profileId);
            
            if (profileHistory == null) return false;

            // Find the latest unfinished transfers (ChangelistNumber = 0)
            var unfinishedTransfers = profileHistory.Syncs
                .Where(s => s.ChangelistNumber == 0)
                .OrderByDescending(s => s.SyncTime)
                .FirstOrDefault();

            if (unfinishedTransfers == null) return false;

            // Update the changelist number
            unfinishedTransfers.ChangelistNumber = changelistNumber;

            // Save the updated histories
            SaveHistoriesForDate(histories, today);
            
            return true;
        }

        /// <summary>
        /// Updates the ChangelistNumber of the latest unfinished P4SyncedTransfers for the specified SyncHistory.
        /// If no latest unfinished transfers are found, the operation is skipped.
        /// </summary>
        /// <param name="syncHistory">The sync history to update</param>
        /// <param name="changelistNumber">The changelist number to set</param>
        /// <returns>True if the update was successful, false if no unfinished transfers were found</returns>
        public bool UpdateLatestUnfinishedChangelistNumber(SyncHistory syncHistory, int changelistNumber)
        {
            if (!enableFileWriting || syncHistory?.Syncs == null) return false;

            // Find the latest unfinished transfers (ChangelistNumber = 0)
            var unfinishedTransfers = syncHistory.Syncs
                .Where(s => s.ChangelistNumber == 0)
                .OrderByDescending(s => s.SyncTime)
                .FirstOrDefault();

            if (unfinishedTransfers == null) return false;

            // Update the changelist number
            unfinishedTransfers.ChangelistNumber = changelistNumber;

            // Determine which date file to save to based on the sync time
            var syncDate = unfinishedTransfers.SyncTime.Date;
            var histories = LoadHistoriesForDate(syncDate);
            
            // Find and update the corresponding history in the loaded histories
            var existingHistory = histories.FirstOrDefault(h => h.ProfileId == syncHistory.ProfileId);
            if (existingHistory != null)
            {
                // Update the existing history with the modified sync history
                var existingSync = existingHistory.Syncs.FirstOrDefault(s => s == unfinishedTransfers);
                if (existingSync != null)
                {
                    existingSync.ChangelistNumber = changelistNumber;
                }
            }

            // Save the updated histories
            SaveHistoriesForDate(histories, syncDate);
            
            return true;
        }

        /// <summary>
        /// Computes a unique ID for a sync profile based on its content
        /// </summary>
        /// <param name="profile">The sync profile</param>
        /// <returns>Profile ID as string</returns>
        public string ComputeProfileId(SyncProfile? profile)
        {
            if (profile == null) return string.Empty;
            var json = JsonSerializer.Serialize(profile);
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(json));
            return new Guid(hash.Take(16).ToArray()).ToString();
        }
    }
}