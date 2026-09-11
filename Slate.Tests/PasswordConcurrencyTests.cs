using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Slate.Core;

namespace Slate.Tests;

internal static class PasswordConcurrencyTests
{
    private const string Origin = "https://concurrency.example.test";
    private const string Synthetic = "Synthetic-Password!123";

    private static void Assert(bool value, string message = "Assertion failed.")
    {
        if (!value) throw new Exception(message);
    }

    private static T Get<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"Expected rejection with {typeof(T).Name}.");
    }

    private sealed class ConcurrencyTestProtector : ICredentialProtector
    {
        private readonly Dictionary<(string CiphertextHex, string EntropyHex), byte[]> _values = [];
        private readonly object _lock = new();

        public byte[] Protect(byte[] plaintext, byte[]? entropy = null)
        {
            var token = Guid.NewGuid().ToByteArray();
            var tokenHex = Convert.ToHexString(token);
            var entropyHex = entropy is not null ? Convert.ToHexString(entropy) : string.Empty;
            lock (_lock)
            {
                _values[(tokenHex, entropyHex)] = plaintext.ToArray();
            }
            return token;
        }

        public byte[] Unprotect(byte[] ciphertext, byte[]? entropy = null)
        {
            var tokenHex = Convert.ToHexString(ciphertext);
            var entropyHex = entropy is not null ? Convert.ToHexString(entropy) : string.Empty;
            byte[]? plaintext;
            lock (_lock)
            {
                if (!_values.TryGetValue((tokenHex, entropyHex), out plaintext))
                    throw new CryptographicException("Decryption failed: invalid token or entropy mismatch.");
            }
            return plaintext.ToArray();
        }

        public byte[] Protect(byte[] plaintext) => Protect(plaintext, null);
        public byte[] Unprotect(byte[] ciphertext) => Unprotect(ciphertext, null);
    }

    private static void WithVault(Action<CredentialVault, string, ConcurrencyTestProtector> test)
    {
        string folder = Path.Combine(Path.GetTempPath(), "slate-concurrency-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var protector = new ConcurrencyTestProtector();
        try
        {
            test(new CredentialVault(folder, protector), folder, protector);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                try { Directory.Delete(folder, true); } catch { /* best effort cleanup */ }
            }
        }
    }

    public static void Run(Action<string, Action> check)
    {
        check("Concurrency: Rapid sequential writes maintain atomic replacement and anchor synchronization", () => WithVault((v, folder, p) =>
        {
            string path = Path.Combine(folder, "credentials.v2.json");
            string anchorPath = Path.Combine(folder, "vault.anchor");
            var cred = Get(v.SaveAsync(Origin, "rapid_user", Synthetic + "_0", null));

            for (int i = 1; i <= 50; i++)
            {
                cred = Get(v.SaveAsync(Origin, "rapid_user", Synthetic + "_" + i, cred));
                Assert(cred.Revision == i + 1, $"Expected credential revision {i + 1}, got {cred.Revision}");

                Assert(File.Exists(path), "Primary vault file missing.");
                Assert(!File.Exists(path + ".tmp"), "Orphaned primary .tmp file exists.");
                Assert(File.Exists(anchorPath), "Anchor file missing.");
                Assert(!File.Exists(anchorPath + ".tmp"), "Orphaned anchor .tmp file exists.");

                var dbJson = JsonNode.Parse(File.ReadAllText(path))!;
                var anchorJson = JsonNode.Parse(File.ReadAllText(anchorPath))!;

                long dbRev = dbJson["VaultRevision"]!.GetValue<long>();
                long anchorRev = anchorJson["HighWaterRevision"]!.GetValue<long>();
                string dbDigest = dbJson["EntriesDigest"]!.GetValue<string>();
                string anchorDigest = anchorJson["EntriesDigest"]!.GetValue<string>();

                Assert(dbRev == i + 1, $"Database revision {dbRev} mismatch with expected {i + 1}");
                Assert(anchorRev == i + 1, $"Anchor revision {anchorRev} mismatch with expected {i + 1}");
                Assert(string.Equals(dbDigest, anchorDigest, StringComparison.OrdinalIgnoreCase), "Digest mismatch between primary and anchor");
            }

            var reloaded = new CredentialVault(folder, p);
            var list = Get(reloaded.ListAsync());
            Assert(list.Count == 1, $"Expected 1 entry, got {list.Count}");
            Assert(Get(reloaded.RevealAsync(cred.Id, cred.Revision)) == Synthetic + "_50", "Revealed password did not match final update.");
        }));

        check("Concurrency: Semaphore isolation ensures 50 concurrent saves succeed without loss or corruption", () => WithVault((v, folder, p) =>
        {
            const int Concurrency = 50;
            string path = Path.Combine(folder, "credentials.v2.json");
            string anchorPath = Path.Combine(folder, "vault.anchor");

            var tasks = Enumerable.Range(0, Concurrency).Select(i =>
                Task.Run(() => v.SaveAsync(Origin, $"concurrent_user_{i:D3}", $"{Synthetic}_{i}", null))
            ).ToArray();

            Task.WaitAll(tasks);

            var inMemory = Get(v.ListAsync());
            Assert(inMemory.Count == Concurrency, $"Expected {Concurrency} in-memory entries, got {inMemory.Count}");
            Assert(inMemory.Select(m => m.Username).Distinct().Count() == Concurrency, "Duplicate usernames found in in-memory list.");

            var fresh = new CredentialVault(folder, p);
            var onDisk = Get(fresh.ListAsync());
            Assert(onDisk.Count == Concurrency, $"Expected {Concurrency} on-disk entries, got {onDisk.Count}");

            var dbJson = JsonNode.Parse(File.ReadAllText(path))!;
            var anchorJson = JsonNode.Parse(File.ReadAllText(anchorPath))!;

            long dbRev = dbJson["VaultRevision"]!.GetValue<long>();
            long anchorRev = anchorJson["HighWaterRevision"]!.GetValue<long>();
            string dbDigest = dbJson["EntriesDigest"]!.GetValue<string>();
            string anchorDigest = anchorJson["EntriesDigest"]!.GetValue<string>();

            Assert(dbRev == Concurrency, $"Expected final VaultRevision {Concurrency}, got {dbRev}");
            Assert(anchorRev == Concurrency, $"Expected final AnchorRevision {Concurrency}, got {anchorRev}");
            Assert(string.Equals(dbDigest, anchorDigest, StringComparison.OrdinalIgnoreCase), "Final digest mismatch between primary and anchor.");
            Assert(!File.Exists(path + ".tmp"), "Orphaned primary .tmp file remains.");
            Assert(!File.Exists(anchorPath + ".tmp"), "Orphaned anchor .tmp file remains.");
        }));

        check("Concurrency: Optimistic conflict rejection on concurrent duplicate account creation", () => WithVault((v, _, _) =>
        {
            const int Racers = 20;
            int successes = 0;
            int conflicts = 0;

            var tasks = Enumerable.Range(0, Racers).Select(i => Task.Run(async () =>
            {
                try
                {
                    await v.SaveAsync(Origin, "racing_alice", $"{Synthetic}_{i}", null);
                    Interlocked.Increment(ref successes);
                }
                catch (CredentialConflictException)
                {
                    Interlocked.Increment(ref conflicts);
                }
            })).ToArray();

            Task.WaitAll(tasks);

            Assert(successes == 1, $"Expected exactly 1 success, got {successes}");
            Assert(conflicts == Racers - 1, $"Expected {Racers - 1} conflicts, got {conflicts}");

            var list = Get(v.ListAsync(Origin));
            Assert(list.Count == 1, $"Expected 1 entry, got {list.Count}");
            Assert(list[0].Username == "racing_alice");
            Assert(list[0].Revision == 1);
        }));

        check("Concurrency: Optimistic conflict rejection on concurrent updates to the same record", () => WithVault((v, _, _) =>
        {
            var initial = Get(v.SaveAsync(Origin, "alice_update_race", Synthetic, null));
            Assert(initial.Revision == 1);

            const int Racers = 20;
            int successes = 0;
            int conflicts = 0;

            var tasks = Enumerable.Range(0, Racers).Select(i => Task.Run(async () =>
            {
                try
                {
                    await v.SaveAsync(Origin, "alice_update_race", $"{Synthetic}_{i}", initial);
                    Interlocked.Increment(ref successes);
                }
                catch (CredentialConflictException)
                {
                    Interlocked.Increment(ref conflicts);
                }
            })).ToArray();

            Task.WaitAll(tasks);

            Assert(successes == 1, $"Expected exactly 1 success, got {successes}");
            Assert(conflicts == Racers - 1, $"Expected {Racers - 1} conflicts, got {conflicts}");

            var list = Get(v.ListAsync(Origin));
            Assert(list.Count == 1);
            Assert(list[0].Revision == 2);
        }));

        check("Concurrency: Optimistic conflict rejection on concurrent delete vs update race", () => WithVault((v, _, _) =>
        {
            var initial = Get(v.SaveAsync(Origin, "alice_del_race", Synthetic, null));

            int updateWins = 0;
            int deleteWins = 0;
            int conflicts = 0;

            var t1 = Task.Run(async () =>
            {
                try
                {
                    await v.UpdateAsync(initial.Id, initial.Revision, "alice_del_race", Synthetic + "_updated");
                    Interlocked.Increment(ref updateWins);
                }
                catch (CredentialConflictException)
                {
                    Interlocked.Increment(ref conflicts);
                }
            });

            var t2 = Task.Run(async () =>
            {
                try
                {
                    await v.DeleteAsync(initial.Id, initial.Revision);
                    Interlocked.Increment(ref deleteWins);
                }
                catch (CredentialConflictException)
                {
                    Interlocked.Increment(ref conflicts);
                }
            });

            Task.WaitAll(t1, t2);

            Assert(updateWins + deleteWins == 1, $"Expected exactly 1 winner, got {updateWins + deleteWins}");
            Assert(conflicts == 1, $"Expected exactly 1 conflict, got {conflicts}");

            var list = Get(v.ListAsync());
            if (updateWins == 1)
            {
                Assert(list.Count == 1);
                Assert(list[0].Revision == 2);
            }
            else
            {
                Assert(list.Count == 0);
            }
        }));

        check("Concurrency: Optimistic conflict rejection on batch save with mixed valid and stale items", () => WithVault((v, folder, p) =>
        {
            var alice = Get(v.SaveAsync(Origin, "alice_batch", Synthetic, null));
            var bob = Get(v.SaveAsync(Origin, "bob_batch", Synthetic, null));

            var alice2 = Get(v.SaveAsync(Origin, "alice_batch", Synthetic + "_2", alice));
            Assert(alice2.Revision == 2);

            var batch = new[]
            {
                new CredentialSaveRequest(Origin, "charlie_batch", Synthetic, null),
                new CredentialSaveRequest(Origin, "alice_batch", Synthetic + "_stale", alice)
            };

            Throws<CredentialConflictException>(() => Get(v.SaveBatchAsync(batch)));

            var list = Get(v.ListAsync());
            Assert(list.Count == 2, $"Expected 2 entries, got {list.Count}");
            Assert(!list.Any(c => c.Username == "charlie_batch"), "Charlie was partially saved despite batch conflict!");
            Assert(Get(v.RevealAsync(alice.Id, 2)) == Synthetic + "_2", "Alice was modified despite batch conflict!");

            var fresh = new CredentialVault(folder, p);
            var freshList = Get(fresh.ListAsync());
            Assert(freshList.Count == 2);
            Assert(!freshList.Any(c => c.Username == "charlie_batch"));
        }));

        check("Durability: Read-only backup recovery strictly blocks all write mutations and preserves damaged primary", () => WithVault((v, folder, p) =>
        {
            var first = Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            var second = Get(v.SaveAsync(Origin, "bob", Synthetic + "_bob", null));

            string path = Path.Combine(folder, "credentials.v2.json");
            string bakPath = path + ".bak";
            Assert(File.Exists(path) && File.Exists(bakPath), "Primary and backup files must exist.");

            const string DamagedContent = "{\"Corrupted\": true, \"Truncated\": [1, 2, 3";
            File.WriteAllText(path, DamagedContent);

            var recovered = new CredentialVault(folder, p);
            var list = Get(recovered.ListAsync());
            Assert(recovered.RecoveredFromBackup, "Vault did not report RecoveredFromBackup = true");
            Assert(list.Count == 1 && list[0].Username == "alice", "Expected backup entries (alice only).");
            Assert(Get(recovered.RevealAsync(first.Id, first.Revision)) == Synthetic, "Password revelation from backup failed.");
            Assert(Get(recovered.AssessAsync(Origin, "alice", Synthetic)).Change == CredentialChange.Unchanged, "Assess on backup failed.");

            Throws<CredentialVaultException>(() => Get(recovered.SaveAsync(Origin, "charlie", Synthetic, null)));
            Throws<CredentialVaultException>(() => Get(recovered.SaveBatchAsync([new CredentialSaveRequest(Origin, "charlie", Synthetic, null)])));
            Throws<CredentialVaultException>(() => Get(recovered.UpdateAsync(first.Id, first.Revision, "alice_new", Synthetic)));
            Throws<CredentialVaultException>(() => Get(recovered.DeleteAsync(first.Id, first.Revision)));
            Throws<CredentialVaultException>(() => Get(recovered.MarkUsedAsync(first.Id, first.Revision)));

            Assert(File.ReadAllText(path) == DamagedContent, "Damaged primary was modified or overwritten!");
            Assert(File.Exists(bakPath), "Backup file was removed!");
        }));

        check("Durability: Read-only backup recovery when primary file is missing blocks all writes", () => WithVault((v, folder, p) =>
        {
            var first = Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            Get(v.SaveAsync(Origin, "bob", Synthetic, null));

            string path = Path.Combine(folder, "credentials.v2.json");
            string bakPath = path + ".bak";
            Assert(File.Exists(path) && File.Exists(bakPath));

            File.Delete(path);
            Assert(!File.Exists(path), "Primary file should be deleted.");

            var recovered = new CredentialVault(folder, p);
            var list = Get(recovered.ListAsync());
            Assert(recovered.RecoveredFromBackup, "Vault did not report RecoveredFromBackup = true when primary missing.");
            Assert(list.Count == 1);

            Throws<CredentialVaultException>(() => Get(recovered.SaveAsync(Origin, "charlie", Synthetic, null)));
            Throws<CredentialVaultException>(() => Get(recovered.DeleteAsync(first.Id, first.Revision)));

            Assert(!File.Exists(path), "Primary file should not be created by failed write.");
        }));

        check("Durability: Crash recovery when primary replacement committed before anchor self-heals forward", () => WithVault((v, folder, p) =>
        {
            var first = Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            var second = Get(v.SaveAsync(Origin, "alice", Synthetic + "_2", first));

            string path = Path.Combine(folder, "credentials.v2.json");
            string anchorPath = Path.Combine(folder, "vault.anchor");

            var primaryJson = JsonNode.Parse(File.ReadAllText(path))!;
            var anchorJson = JsonNode.Parse(File.ReadAllText(anchorPath))!;
            anchorJson["HighWaterRevision"] = 1;
            File.WriteAllText(anchorPath, anchorJson.ToJsonString());

            var healedVault = new CredentialVault(folder, p);
            var list = Get(healedVault.ListAsync());
            Assert(list.Count == 1);
            Assert(list[0].Revision == 2);

            var reloadedAnchor = JsonNode.Parse(File.ReadAllText(anchorPath))!;
            Assert(reloadedAnchor["HighWaterRevision"]!.GetValue<long>() == 2, "Anchor was not self-healed forward.");
            Assert(string.Equals(
                reloadedAnchor["EntriesDigest"]!.GetValue<string>(),
                primaryJson["EntriesDigest"]!.GetValue<string>(),
                StringComparison.OrdinalIgnoreCase), "Anchor digest does not match primary digest.");

            var third = Get(healedVault.SaveAsync(Origin, "alice", Synthetic + "_3", list[0]));
            Assert(third.Revision == 3);
        }));

        check("Durability: Rollback attack with older primary file strictly fails closed without backup fallback", () => WithVault((v, folder, p) =>
        {
            var first = Get(v.SaveAsync(Origin, "alice", Synthetic, null));
            var second = Get(v.SaveAsync(Origin, "alice", Synthetic + "_2", first));

            string path = Path.Combine(folder, "credentials.v2.json");
            string backupJson = File.ReadAllText(path + ".bak");
            File.WriteAllText(path, backupJson);

            var attackedVault = new CredentialVault(folder, p);

            Throws<CredentialVaultException>(() => Get(attackedVault.ListAsync()));
            Throws<CredentialVaultException>(() => Get(attackedVault.RevealAsync(first.Id, 1)));
            Assert(!attackedVault.RecoveredFromBackup, "Vault incorrectly fell back to backup during rollback attack.");
        }));
    }
}
