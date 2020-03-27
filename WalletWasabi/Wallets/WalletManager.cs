using NBitcoin;
using NBitcoin.Protocol;
using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WalletWasabi.BitcoinCore;
using WalletWasabi.Blockchain.Analysis.Clustering;
using WalletWasabi.Blockchain.Analysis.FeesEstimation;
using WalletWasabi.Blockchain.Keys;
using WalletWasabi.Blockchain.TransactionOutputs;
using WalletWasabi.Blockchain.TransactionProcessing;
using WalletWasabi.Blockchain.Transactions;
using WalletWasabi.CoinJoin.Client.Clients.Queuing;
using WalletWasabi.Helpers;
using WalletWasabi.Logging;
using WalletWasabi.Models;
using WalletWasabi.Services;
using WalletWasabi.Stores;

namespace WalletWasabi.Wallets
{
	public class WalletManager
	{
		public WalletManager(Network network, WalletDirectories walletDirectories)
		{
			using (BenchmarkLogger.Measure())
			{
				Network = Guard.NotNull(nameof(network), network);
				WalletDirectories = Guard.NotNull(nameof(walletDirectories), walletDirectories);
				Wallets = new Dictionary<Wallet, HashSet<uint256>>();
				Lock = new object();
				StartStopWalletLock = new AsyncLock();
				CancelAllInitialization = new CancellationTokenSource();
				RefreshWalletList();
			}
		}

		/// <summary>
		/// Triggered if any of the Wallets processes a transaction. The sender of the event will be the Wallet.
		/// </summary>
		public event EventHandler<ProcessedResult> WalletRelevantTransactionProcessed;

		/// <summary>
		/// Triggered if any of the Wallets dequeues one or more coins. The sender of the event will be the Wallet.
		/// </summary>
		public event EventHandler<DequeueResult> OnDequeue;

		/// <summary>
		/// Triggered if any of the Wallets changes its state. The sender of the event will be the Wallet.
		/// </summary>
		public event EventHandler<WalletState> WalletStateChanged;

		/// <summary>
		/// Triggered if a wallet added to the Wallet collection. The sender of the event will be the WalletManager and the argument is the added Wallet.
		/// </summary>
		public event EventHandler<Wallet> WalletAdded;

		private CancellationTokenSource CancelAllInitialization { get; }

		private Dictionary<Wallet, HashSet<uint256>> Wallets { get; }
		private object Lock { get; }
		private AsyncLock StartStopWalletLock { get; }

		private BitcoinStore BitcoinStore { get; set; }
		private WasabiSynchronizer Synchronizer { get; set; }
		private NodesGroup Nodes { get; set; }
		private ServiceConfiguration ServiceConfiguration { get; set; }

		private IFeeProvider FeeProvider { get; set; }
		private CoreNode BitcoinCoreNode { get; set; }
		public Network Network { get; }
		public WalletDirectories WalletDirectories { get; }

		private void RefreshWalletList()
		{
			foreach (var fileInfo in WalletDirectories.EnumerateWalletFiles())
			{
				try
				{
					string walletName = Path.GetFileNameWithoutExtension(fileInfo.FullName);
					lock (Lock)
					{
						if (Wallets.Any(w => w.Key.WalletName == walletName))
						{
							continue;
						}
					}
					AddWallet(walletName);
				}
				catch (Exception ex)
				{
					Logger.LogWarning(ex);
				}
			}
		}

		public void SignalQuitPending(bool isQuitPending)
		{
			lock (Lock)
			{
				foreach (var client in Wallets.Keys.Where(x => x.ChaumianClient is { }).Select(x => x.ChaumianClient))
				{
					client.IsQuitPending = isQuitPending;
				}
			}
		}

		/// <param name="refreshWalletList">Refreshes wallet list from files.</param>
		public IEnumerable<KeyManager> GetKeyManagers(bool refreshWalletList = true)
		{
			var wallets = GetWallets(refreshWalletList);

			return wallets.Select(x => x.KeyManager);
		}

		public IEnumerable<Wallet> GetWallets(bool refreshWalletList = true)
		{
			if (refreshWalletList)
			{
				RefreshWalletList();
			}

			lock (Lock)
			{
				return Wallets.Keys
					.ToList();
			}
		}

		public IEnumerable<SmartLabel> GetLabels()
		{
			// Don't refresh wallet list as it may be slow.
			var labels = GetKeyManagers(refreshWalletList: false)
				.SelectMany(x => x.GetLabels());

			var txStore = BitcoinStore?.TransactionStore;
			if (txStore is { })
			{
				labels = labels.Concat(txStore.GetLabels());
			}

			return labels;
		}

		public Wallet GetFirstOrDefaultWallet()
		{
			lock (Lock)
			{
				return Wallets.Keys.FirstOrDefault(x => x.State == WalletState.Started);
			}
		}

		public bool AnyWallet()
		{
			return AnyWallet(x => x.State >= WalletState.Starting);
		}

		public bool AnyWallet(Func<Wallet, bool> predicate)
		{
			lock (Lock)
			{
				return Wallets.Keys.Any(predicate);
			}
		}

		public async Task<Wallet> StartWalletAsync(Wallet wallet)
		{
			Guard.NotNull(nameof(wallet), wallet);

			lock (Lock)
			{
				// Throw an exception if the wallet was not added to the WalletManager.
				Wallets.Single(x => x.Key == wallet);
			}

			wallet.RegisterServices(BitcoinStore, Synchronizer, Nodes, ServiceConfiguration, FeeProvider, BitcoinCoreNode);

			using (await StartStopWalletLock.LockAsync(CancelAllInitialization.Token).ConfigureAwait(false))
			{
				try
				{
					var cancel = CancelAllInitialization.Token;
					Logger.LogInfo($"Starting {nameof(Wallet)}...");
					await wallet.StartAsync(cancel).ConfigureAwait(false);
					Logger.LogInfo($"{nameof(Wallet)} started.");
					cancel.ThrowIfCancellationRequested();

					return wallet;
				}
				catch
				{
					await wallet.StopAsync(CancellationToken.None).ConfigureAwait(false);
					throw;
				}
			}
		}

		public Task<Wallet> StartWalletAsync(KeyManager keyManagerToFindByReference)
		{
			Wallet wallet;
			lock (Lock)
			{
				wallet = Wallets.Single(x => x.Key.KeyManager == keyManagerToFindByReference).Key;
			}

			return StartWalletAsync(wallet);
		}

		public Task<Wallet> AddAndStartWalletAsync(KeyManager keyManager)
		{
			var wallet = AddWallet(keyManager);
			return StartWalletAsync(wallet);
		}

		public Wallet AddWallet(KeyManager keyManager)
		{
			Wallet wallet = new Wallet(WalletDirectories.WorkDir, Network, keyManager);
			AddWallet(wallet);
			return wallet;
		}

		private Wallet AddWallet(string walletName)
		{
			(string walletFullPath, string walletBackupFullPath) = WalletDirectories.GetWalletFilePaths(walletName);
			Wallet wallet;
			try
			{
				wallet = new Wallet(WalletDirectories.WorkDir, Network, walletFullPath);
			}
			catch (Exception ex)
			{
				if (!File.Exists(walletBackupFullPath))
				{
					throw;
				}

				Logger.LogWarning($"Wallet got corrupted.\n" +
					$"Wallet Filepath: {walletFullPath}\n" +
					$"Trying to recover it from backup.\n" +
					$"Backup path: {walletBackupFullPath}\n" +
					$"Exception: {ex}");
				if (File.Exists(walletFullPath))
				{
					string corruptedWalletBackupPath = $"{walletBackupFullPath}_CorruptedBackup";
					if (File.Exists(corruptedWalletBackupPath))
					{
						File.Delete(corruptedWalletBackupPath);
						Logger.LogInfo($"Deleted previous corrupted wallet file backup from `{corruptedWalletBackupPath}`.");
					}
					File.Move(walletFullPath, corruptedWalletBackupPath);
					Logger.LogInfo($"Backed up corrupted wallet file to `{corruptedWalletBackupPath}`.");
				}
				File.Copy(walletBackupFullPath, walletFullPath);

				wallet = new Wallet(WalletDirectories.WorkDir, Network, walletFullPath);
			}

			AddWallet(wallet);
			return wallet;
		}

		private void AddWallet(Wallet wallet)
		{
			lock (Lock)
			{
				if (Wallets.Any(w => w.Key.WalletName == wallet.WalletName))
				{
					throw new InvalidOperationException($"Wallet with the same name was already added: {wallet.WalletName}.");
				}
				Wallets.Add(wallet, new HashSet<uint256>());
			}

			if (!File.Exists(WalletDirectories.GetWalletFilePaths(wallet.WalletName).walletFilePath))
			{
				wallet.KeyManager.ToFile();
			}

			wallet.WalletRelevantTransactionProcessed += TransactionProcessor_WalletRelevantTransactionProcessed;
			wallet.OnDequeue += ChaumianClient_OnDequeue;
			wallet.StateChanged += Wallet_StateChanged;

			WalletAdded?.Invoke(this, wallet);
		}

		public async Task DequeueAllCoinsGracefullyAsync(DequeueReason reason, CancellationToken token)
		{
			IEnumerable<Task> tasks = null;
			lock (Lock)
			{
				tasks = Wallets.Keys.Where(x => x.ChaumianClient is { }).Select(x => x.ChaumianClient.DequeueAllCoinsFromMixGracefullyAsync(reason, token)).ToArray();
			}
			await Task.WhenAll(tasks).ConfigureAwait(false);
		}

		private void ChaumianClient_OnDequeue(object sender, DequeueResult e)
		{
			OnDequeue?.Invoke(sender, e);
		}

		private void TransactionProcessor_WalletRelevantTransactionProcessed(object sender, ProcessedResult e)
		{
			WalletRelevantTransactionProcessed?.Invoke(sender, e);
		}

		private void Wallet_StateChanged(object sender, WalletState e)
		{
			WalletStateChanged?.Invoke(sender, e);
		}

		public async Task RemoveAndStopAllAsync(CancellationToken cancel)
		{
			try
			{
				CancelAllInitialization?.Cancel();
				CancelAllInitialization?.Dispose();
			}
			catch (ObjectDisposedException)
			{
				Logger.LogWarning($"{nameof(CancelAllInitialization)} is disposed. This can occur due to an error while processing the wallet.");
			}

			using (await StartStopWalletLock.LockAsync(cancel).ConfigureAwait(false))
			{
				List<Wallet> walletsListClone;
				lock (Lock)
				{
					walletsListClone = Wallets.Keys.ToList();
				}
				foreach (var wallet in walletsListClone)
				{
					if (cancel.IsCancellationRequested)
					{
						return;
					}

					wallet.WalletRelevantTransactionProcessed -= TransactionProcessor_WalletRelevantTransactionProcessed;
					wallet.OnDequeue -= ChaumianClient_OnDequeue;
					wallet.StateChanged -= Wallet_StateChanged;

					lock (Lock)
					{
						if (!Wallets.Remove(wallet))
						{
							throw new InvalidOperationException("Wallet service doesn't exist.");
						}
					}

					try
					{
						var keyManager = wallet.KeyManager;
						string backupWalletFilePath = WalletDirectories.GetWalletFilePaths(Path.GetFileName(keyManager.FilePath)).walletBackupFilePath;
						keyManager.ToFile(backupWalletFilePath);
						Logger.LogInfo($"{nameof(wallet.KeyManager)} backup saved to `{backupWalletFilePath}`.");
						await wallet.StopAsync(cancel).ConfigureAwait(false);
						wallet?.Dispose();
						Logger.LogInfo($"{nameof(Wallet)} is stopped.");
					}
					catch (Exception ex)
					{
						Logger.LogError(ex);
					}
				}
			}
		}

		public void ProcessCoinJoin(SmartTransaction tx)
		{
			lock (Lock)
			{
				foreach (var pair in Wallets.Where(x => x.Key.State == WalletState.Started && !x.Value.Contains(tx.GetHash())))
				{
					var wallet = pair.Key;
					pair.Value.Add(tx.GetHash());
					wallet.TransactionProcessor.Process(tx);
				}
			}
		}

		public void Process(SmartTransaction transaction)
		{
			lock (Lock)
			{
				foreach (var wallet in Wallets.Where(x => x.Key.State == WalletState.Started))
				{
					wallet.Key.TransactionProcessor.Process(transaction);
				}
			}
		}

		public IEnumerable<SmartCoin> CoinsByOutPoint(OutPoint input)
		{
			lock (Lock)
			{
				var res = new List<SmartCoin>();
				foreach (var wallet in Wallets.Where(x => x.Key.State == WalletState.Started))
				{
					SmartCoin coin = wallet.Key.Coins.GetByOutPoint(input);
					res.Add(coin);
				}

				return res;
			}
		}

		public ISet<uint256> FilterUnknownCoinjoins(IEnumerable<uint256> cjs)
		{
			lock (Lock)
			{
				var unknowns = new HashSet<uint256>();
				foreach (var pair in Wallets.Where(x => x.Key.State == WalletState.Started))
				{
					// If a wallet service doesn't know about the tx, then we add it for processing.
					foreach (var tx in cjs.Where(x => !pair.Value.Contains(x)))
					{
						unknowns.Add(tx);
					}
				}
				return unknowns;
			}
		}

		public void RegisterServices(BitcoinStore bitcoinStore, WasabiSynchronizer synchronizer, NodesGroup nodes, ServiceConfiguration serviceConfiguration, IFeeProvider feeProvider, CoreNode bitcoinCoreNode)
		{
			BitcoinStore = bitcoinStore;
			Synchronizer = synchronizer;
			Nodes = nodes;
			ServiceConfiguration = serviceConfiguration;
			FeeProvider = feeProvider;
			BitcoinCoreNode = bitcoinCoreNode;
		}

		/// <param name="refreshWalletList">Refreshes wallet list from files.</param>
		public Wallet GetWalletByName(string walletName, bool refreshWalletList = true)
		{
			if (refreshWalletList)
			{
				RefreshWalletList();
			}
			lock (Lock)
			{
				return Wallets.Keys.Single(x => x.KeyManager.WalletName == walletName);
			}
		}
	}
}