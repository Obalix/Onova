using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Onova.Exceptions;
using Onova.Internal;
using Onova.Internal.Extensions;
using Onova.Models;
using Onova.Services;

namespace Onova
{
    /// <summary>
    /// Entry point for handling application updates.
    /// </summary>
    public class UpdateManager : IUpdateManager
	{
		private const string UpdaterResourceName = "Onova.Updater.exe";

		private readonly IPackageResolver _resolver;
		private readonly IPackageExtractor _extractor;

		private readonly string _storageDirPath;
		private readonly string _updaterFilePath;
		private readonly string _lockFilePath;

		private LockFile? _lockFile;
		private bool _isDisposed;
		private readonly TextWriter _log;

		#region [Constructors]

		/// <summary>
		/// Initializes an instance of <see cref="UpdateManager"/>.
		/// </summary>
		[SuppressMessage("CodeQuality", "IDE0079", Justification = "Suppression necessary for netcore31")]
		public UpdateManager(AssemblyMetadata updatee, IPackageResolver resolver, IPackageExtractor extractor)
		{
			Platform.EnsureWindows();

			this.Updatee = updatee;
			this._resolver = resolver;
			this._extractor = extractor;

			// Set storage directory path
			this._storageDirPath = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"Onova",
				updatee.Name
			);

			// Set updater executable file path
			this._updaterFilePath = Path.Combine(this._storageDirPath, $"{updatee.Name}.Updater.exe");

			// Set lock file path
			this._lockFilePath = Path.Combine(this._storageDirPath, "Onova.lock");

			this._log = File.CreateText(Path.Combine(this._storageDirPath, $"{ updatee.Name}.UpdateManagerLog.txt"));

			this.WriteLog("UpdateManager initialized");
		}

		/// <summary>
		/// Initializes an instance of <see cref="UpdateManager"/> on the entry assembly.
		/// </summary>
		public UpdateManager(IPackageResolver resolver, IPackageExtractor extractor)
			: this(AssemblyMetadata.FromEntryAssembly(), resolver, extractor)
		{
		}

		#endregion

		#region [IUpdateManager implementation]

		/// <inheritdoc />
		public AssemblyMetadata Updatee { get; }

		/// <inheritdoc />
		public async Task<CheckForUpdatesResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
		{
			try
			{
				this.WriteLog("CheckForUpdatesAsync - started");

				// Ensure that the current state is valid for this operation
				this.EnsureNotDisposed();

				// Get versions
				var versions = await this._resolver.GetPackageVersionsAsync(cancellationToken);
				var lastVersion = versions.Max();
				var canUpdate = lastVersion != null && this.Updatee.Version < lastVersion;


				var result = new CheckForUpdatesResult(versions, lastVersion, canUpdate);
				this.WriteLog("CheckForUpdatesAsync - started");
				return result;
			}
			catch (Exception ex)
			{
				if (!(ex is OperationCanceledException))
				{
					this.WriteLog($"CheckForUpdatesAsync - failed", ex);
				}
				throw;
			}
		}

		/// <inheritdoc />
		public bool IsUpdatePrepared(Version version)
		{
			try
			{
				// Ensure that the current state is valid for this operation
				this.EnsureNotDisposed();

				// Get package file path and content directory path
				var packageFilePath = this.GetPackageFilePath(version);
				var packageContentDirPath = this.GetPackageContentDirPath(version);

				// Package content directory should exist
				// Package file should have been deleted after extraction
				// Updater file should exist
				var result = !File.Exists(packageFilePath)
					&& Directory.Exists(packageContentDirPath)
					&& File.Exists(this._updaterFilePath);

				this.WriteLog($"IsUpdatePrepared - finished");

				return result;
			}
			catch (Exception ex)
			{
				this.WriteLog($"CheckForUpdatesAsync - failed", ex);
				throw;
			}
		}

		/// <inheritdoc />
		public IReadOnlyList<Version> GetPreparedUpdates()
		{
			try
			{
				this.WriteLog("GetPreparedUpdates - started");

				// Ensure that the current state is valid for this operation
				this.EnsureNotDisposed();

				var result = new List<Version>();

				// Enumerate all immediate directories in storage
				if (Directory.Exists(this._storageDirPath))
				{
					foreach (var packageContentDirPath in Directory.EnumerateDirectories(this._storageDirPath))
					{
						// Get directory name
						var packageContentDirName = Path.GetFileName(packageContentDirPath);

						// Try to extract version out of the name
						if (
							string.IsNullOrWhiteSpace(packageContentDirName)
							|| !Version.TryParse(packageContentDirName, out var version)
						)
						{
							continue;
						}

						// If this package is prepared - add it to the list
						if (this.IsUpdatePrepared(version))
						{
							result.Add(version);
						}
					}
				}

				this.WriteLog("GetPreparedUpdates - finished");

				return result;
			}
			catch (Exception ex)
			{
				this.WriteLog($"GetPreparedUpdates - failed", ex);
				throw;
			}
		}

		/// <inheritdoc />
		public async Task PrepareUpdateAsync(Version version, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
		{
			try
			{
				this.WriteLog("PrepareUpdateAsync - started");

				// Ensure that the current state is valid for this operation
				this.EnsureNotDisposed();
				this.EnsureLockFileAcquired();
				this.EnsureUpdaterNotLaunched();

				// Set up progress mixer
				var progressMixer = progress != null
					? new ProgressMixer(progress)
					: null;

				// Get package file path and content directory path
				var packageFilePath = this.GetPackageFilePath(version);
				var packageContentDirPath = this.GetPackageContentDirPath(version);

				// Ensure storage directory exists
				Directory.CreateDirectory(this._storageDirPath);

				// Download package
				await this._resolver.DownloadPackageAsync(
					version,
					packageFilePath,
					progressMixer?.Split(0.9), // 0% -> 90%
					cancellationToken
				);

				// Ensure package content directory exists and is empty
				DirectoryEx.Reset(packageContentDirPath);

				// Extract package contents
				await this._extractor.ExtractPackageAsync(
					packageFilePath,
					packageContentDirPath,
					progressMixer?.Split(0.1), // 90% -> 100%
					cancellationToken
				);

				// Delete package
				File.Delete(packageFilePath);

				// Extract updater
				await Assembly.GetExecutingAssembly().ExtractManifestResourceAsync(UpdaterResourceName, this._updaterFilePath);

				this.WriteLog("PrepareUpdateAsync - finished");
			}
			catch (Exception ex)
			{
				this.WriteLog($"PrepareUpdateAsync - failed", ex);
				throw;
			}
		}

		/// <inheritdoc />
		public void LaunchUpdater(Version version, bool restart, string restartArguments, string[]? additonalExecutables = null)
		{
			try
			{
				this.WriteLog("LaunchUpdater - started");

				// Ensure that the current state is valid for this operation
				this.EnsureNotDisposed();
				this.EnsureLockFileAcquired();
				this.EnsureUpdaterNotLaunched();
				this.EnsureUpdatePrepared(version);

				var updateeDirPath = Path.GetDirectoryName(this.Updatee.FilePath);

				// Get package content directory path
				var packageContentDirPath = this.GetPackageContentDirPath(version);

				// Get original command line arguments and encode them to avoid issues with quotes
				var routedArgs = restartArguments.GetBytes().ToBase64();

				// get absolute paths to additional executables
				var addExePaths = (additonalExecutables ?? Array.Empty<string>())
					.Select(exe => Path.Combine(updateeDirPath!, exe));
				var addExes = string.Join(";", addExePaths).GetBytes().ToBase64();

				// Prepare arguments
				var updaterArgs = $"\"{this.Updatee.FilePath}\" \"{packageContentDirPath}\" \"{restart}\" \"{routedArgs}\" \"{addExes}\"";

				// Decide if updater needs to be elevated

				var updaterNeedsElevation =
					!string.IsNullOrWhiteSpace(updateeDirPath) &&
					!DirectoryEx.CheckWriteAccess(updateeDirPath);

				// Create updater process start info
				var updaterStartInfo = new ProcessStartInfo
				{
					FileName = _updaterFilePath,
					Arguments = updaterArgs,
					CreateNoWindow = true,
					UseShellExecute = false
				};

				// If updater needs to be elevated - use shell execute with "runas"
				if (updaterNeedsElevation)
				{
					updaterStartInfo.Verb = "runas";
					updaterStartInfo.UseShellExecute = true;
				}

				// Create and start updater process
				var updaterProcess = new Process { StartInfo = updaterStartInfo };
				using (updaterProcess)
				{
					this.WriteLog("LaunchUpdater - Updater starting");
					updaterProcess.Start();
				}

				this.WriteLog("LaunchUpdater - finished");
			}
			catch (Exception ex)
            {
				this.WriteLog($"LaunchUpdater - failed", ex);
				throw;
            }
		}

		#endregion

		private string GetPackageFilePath(Version version) => Path.Combine(this._storageDirPath, $"{version}.onv");

		private string GetPackageContentDirPath(Version version) => Path.Combine(this._storageDirPath, $"{version}");

		private void EnsureNotDisposed()
		{
			if (this._isDisposed)
				throw new ObjectDisposedException(this.GetType().FullName);
		}

		private void EnsureLockFileAcquired()
		{
			// Ensure storage directory exists
			Directory.CreateDirectory(this._storageDirPath);

			// Try to acquire lock file if it's not acquired yet
			this._lockFile ??= LockFile.TryAcquire(this._lockFilePath);

			// If failed to acquire - throw
			if (this._lockFile == null)
				throw new LockFileNotAcquiredException();
		}

		private void EnsureUpdaterNotLaunched()
		{
			// Check whether we have write access to updater executable
			// (this is a reasonably accurate check for whether that process is running)
			if (File.Exists(this._updaterFilePath) && !FileEx.CheckWriteAccess(this._updaterFilePath))
				throw new UpdaterAlreadyLaunchedException();
		}

		private void EnsureUpdatePrepared(Version version)
		{
			if (!this.IsUpdatePrepared(version))
				throw new UpdateNotPreparedException(version);
		}

		internal void WriteLog(string content)
		{
			var date = DateTimeOffset.Now;
			this._log.WriteLine($"{date:dd-MMM-yyyy HH:mm:ss.fff}> {content}");
			this._log.Flush();
		}

		internal void WriteLog(string content, Exception ex) => this.WriteLog($"{content} :: { ex.GetType().Name} - { ex.Message}");

		/// <inheritdoc />
		public void Dispose()
		{
			if (!this._isDisposed)
			{
				this._isDisposed = true;
				this._lockFile?.Dispose();

				this.WriteLog("UpdateManager disposed");
				this._log?.Dispose();
			}
		}
	}
}