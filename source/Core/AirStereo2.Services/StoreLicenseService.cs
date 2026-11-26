using System;
using System.Threading;
using System.Threading.Tasks;
using WinRT.Interop;
using Windows.ApplicationModel;
using Windows.Services.Store;

namespace AirStereo2.Services;

public sealed class StoreLicenseService : IDisposable
{
	private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);

	private readonly object _initializationLock = new object();

	private StoreContext? _context;

	private Task<StoreLicenseSnapshot>? _initializationTask;

	private nint _ownerWindow;

	private int _contextInitialized;

	private bool _disposed;

	private StoreLicenseSnapshot _current = (IsDevelopmentPackage() ? DeveloperBuild() : Unknown());

	public StoreLicenseSnapshot Current => Volatile.Read(in _current);

	public Task<StoreLicenseSnapshot>? InitializationTask => Volatile.Read(in _initializationTask);

	public event EventHandler<StoreLicenseSnapshot>? Changed;

	private static bool IsDevelopmentPackage()
	{
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		//IL_000a: Unknown result type (might be due to invalid IL or missing references)
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Invalid comparison between Unknown and I4
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0011: Invalid comparison between Unknown and I4
		try
		{
			PackageSignatureKind signatureKind = Package.Current.SignatureKind;
			return (int)signatureKind != 3 && (int)signatureKind != 4;
		}
		catch
		{
			return true;
		}
	}

	public Task<StoreLicenseSnapshot> InitializeAsync(nint ownerWindow)
	{
		bool flag = false;
		Task<StoreLicenseSnapshot> task;
		lock (_initializationLock)
		{
			if (ownerWindow != IntPtr.Zero)
			{
				_ownerWindow = ownerWindow;
			}
			task = _initializationTask;
			if (task == null || (task.IsCompleted && (task.IsFaulted || task.IsCanceled || Volatile.Read(in _contextInitialized) == 0)))
			{
				nint ownerWindow2 = ((ownerWindow != IntPtr.Zero) ? ownerWindow : _ownerWindow);
				task = (_initializationTask = InitializeCoreAsync(ownerWindow2));
				flag = true;
			}
		}
		if (flag)
		{
			ResetInitializationTaskWhenUnavailableAsync(task);
		}
		return task;
	}

	public async Task<StoreLicenseSnapshot> RefreshOnAppEntryAsync(nint ownerWindow)
	{
		await InitializeAsync(ownerWindow).ConfigureAwait(continueOnCapturedContext: false);
		return await RefreshAsync().ConfigureAwait(continueOnCapturedContext: false);
	}

	private async Task ResetInitializationTaskWhenUnavailableAsync(Task<StoreLicenseSnapshot> task)
	{
		try
		{
			await task.ConfigureAwait(continueOnCapturedContext: false);
			if (!task.IsFaulted && !task.IsCanceled && Volatile.Read(in _contextInitialized) != 0)
			{
				return;
			}
		}
		catch
		{
		}
		lock (_initializationLock)
		{
			if (_initializationTask == task)
			{
				_initializationTask = null;
			}
		}
	}

	private async Task<StoreLicenseSnapshot> InitializeCoreAsync(nint ownerWindow)
	{
		ThrowIfDisposed();
		if (ownerWindow == IntPtr.Zero)
		{
			throw new ArgumentException("A valid owner window handle is required.", "ownerWindow");
		}
		try
		{
			await _refreshLock.WaitAsync().ConfigureAwait(continueOnCapturedContext: false);
			try
			{
				if (_context == (StoreContext)null)
				{
					StoreContext val = StoreContext.GetDefault();
					try
					{
						InitializeWithWindow.Initialize((object)val, (IntPtr)ownerWindow);
						val.OfflineLicensesChanged += OnOfflineLicensesChanged;
						_context = val;
						Volatile.Write(ref _contextInitialized, 1);
					}
					catch
					{
						try
						{
							val.OfflineLicensesChanged -= OnOfflineLicensesChanged;
						}
						catch
						{
						}
						finally
						{
							_context = null;
							Volatile.Write(ref _contextInitialized, 0);
						}
						throw;
					}
				}
				else
				{
					Volatile.Write(ref _contextInitialized, 1);
				}
			}
			finally
			{
				_refreshLock.Release();
			}
			return Current;
		}
		catch (Exception storeUnavailable)
		{
			SetStoreUnavailable(storeUnavailable);
			return Current;
		}
	}

	public async Task<StoreLicenseSnapshot> RefreshAsync()
	{
		ThrowIfDisposed();
		nint ownerWindow;
		lock (_initializationLock)
		{
			ownerWindow = _ownerWindow;
		}
		if (ownerWindow != IntPtr.Zero)
		{
			await InitializeAsync(ownerWindow).ConfigureAwait(continueOnCapturedContext: false);
			if (Volatile.Read(in _contextInitialized) == 0)
			{
				await InitializeAsync(ownerWindow).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
		await _refreshLock.WaitAsync().ConfigureAwait(continueOnCapturedContext: false);
		try
		{
			if (_context == (StoreContext)null)
			{
				return Current;
			}
			StoreAppLicense val = await WindowsRuntimeSystemExtensions.AsTask<StoreAppLicense>(_context.GetAppLicenseAsync()).ConfigureAwait(continueOnCapturedContext: false);
			DateTimeOffset? expirationDate = ((val.ExpirationDate > DateTimeOffset.MinValue) ? new DateTimeOffset?(val.ExpirationDate) : ((DateTimeOffset?)null));
			StoreEntitlementState storeEntitlementState = StoreLicensePolicy.Evaluate(IsDevelopmentPackage(), storeContextAvailable: true, val.IsActive, val.IsTrial, expirationDate, DateTimeOffset.Now);
			SetCurrent(new StoreLicenseSnapshot(IsStoreContextAvailable: true, val.IsActive, val.IsTrial, expirationDate, BuildStatusText(storeEntitlementState, expirationDate), null, storeEntitlementState));
		}
		catch (Exception ex)
		{
			if (Current.AllowsNewConnections)
			{
				SetCurrent(Current with
				{
					IsStoreContextAvailable = false,
					StatusText = AppendCachedSuffix(Current.StatusText),
					ErrorMessage = ex.Message
				});
			}
			else
			{
				SetStoreUnavailable(ex);
			}
		}
		finally
		{
			_refreshLock.Release();
		}
		return Current;
	}

	public bool TryAuthorizeNewConnection(out string reason)
	{
		if (IsDevelopmentPackage())
		{
			reason = string.Empty;
			return true;
		}
		StoreLicenseSnapshot current = Current;
		LocalizationManager instance = LocalizationManager.Instance;
		if (current.EntitlementState == StoreEntitlementState.Trial)
		{
			DateTimeOffset? expirationDate = current.ExpirationDate;
			if (expirationDate.HasValue)
			{
				DateTimeOffset valueOrDefault = expirationDate.GetValueOrDefault();
				if (valueOrDefault <= DateTimeOffset.Now)
				{
					reason = instance.GetString("License_TrialExpired", "The Microsoft Store trial has expired.");
					return false;
				}
			}
		}
		if (current.AllowsNewConnections)
		{
			reason = string.Empty;
			return true;
		}
		reason = current.EntitlementState switch
		{
			StoreEntitlementState.Expired => instance.GetString("License_TrialExpired", "The Microsoft Store trial has expired."), 
			StoreEntitlementState.Inactive => instance.GetString("License_Inactive", "The Microsoft Store license is inactive."), 
			StoreEntitlementState.Unknown => instance.GetString("License_Checking", "The Microsoft Store license is still being checked."), 
			_ => instance.GetString("License_Unavailable", "The Microsoft Store license could not be verified. Please refresh it in Settings."), 
		};
		return false;
	}

	private async void OnOfflineLicensesChanged(StoreContext sender, object args)
	{
		try
		{
			await RefreshAsync().ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (ObjectDisposedException)
		{
		}
		catch (Exception)
		{
		}
	}

	private void SetCurrent(StoreLicenseSnapshot snapshot)
	{
		if (!(snapshot == Current))
		{
			Volatile.Write(ref _current, snapshot);
			PublishChanged(snapshot);
		}
	}

	private void PublishChanged(StoreLicenseSnapshot snapshot)
	{
		EventHandler<StoreLicenseSnapshot> eventHandler = this.Changed;
		if (eventHandler == null)
		{
			return;
		}
		Delegate[] invocationList = eventHandler.GetInvocationList();
		for (int i = 0; i < invocationList.Length; i++)
		{
			EventHandler<StoreLicenseSnapshot> eventHandler2 = (EventHandler<StoreLicenseSnapshot>)invocationList[i];
			try
			{
				eventHandler2(this, snapshot);
			}
			catch (Exception)
			{
			}
		}
	}

	private void SetStoreUnavailable(Exception exception)
	{
		SetCurrent(new StoreLicenseSnapshot(IsStoreContextAvailable: false, IsActive: false, IsTrial: false, null, IsDevelopmentPackage() ? "Development build active" : "Microsoft Store license unavailable", exception.Message, IsDevelopmentPackage() ? StoreEntitlementState.Development : StoreEntitlementState.StoreUnavailable));
	}

	private static string AppendCachedSuffix(string statusText)
	{
		if (statusText.EndsWith(" (cached)", StringComparison.Ordinal))
		{
			return statusText;
		}
		return statusText + " (cached)";
	}

	private static StoreLicenseSnapshot DeveloperBuild()
	{
		return new StoreLicenseSnapshot(IsStoreContextAvailable: false, IsActive: true, IsTrial: false, null, "Developer build", null, StoreEntitlementState.Development);
	}

	internal void AuthorizeForTesting()
	{
		SetCurrentForTesting(DeveloperBuild());
	}

	internal void SetCurrentForTesting(StoreLicenseSnapshot snapshot)
	{
		Volatile.Write(ref _current, snapshot);
		this.Changed?.Invoke(this, snapshot);
	}

	private static StoreLicenseSnapshot Unknown()
	{
		return new StoreLicenseSnapshot(IsStoreContextAvailable: false, IsActive: false, IsTrial: false, null, "Checking Microsoft Store license", null, StoreEntitlementState.Unknown);
	}

	private static string BuildStatusText(StoreEntitlementState state, DateTimeOffset? expirationDate)
	{
		switch (state)
		{
		case StoreEntitlementState.Purchased:
			return "Purchased";
		case StoreEntitlementState.Trial:
			if (expirationDate.HasValue)
			{
				DateTimeOffset valueOrDefault = expirationDate.GetValueOrDefault();
				return $"Trial active until {valueOrDefault.LocalDateTime:g}";
			}
			return "Trial active";
		case StoreEntitlementState.Expired:
			return "Trial expired";
		default:
			return "Inactive Store license";
		}
	}

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		try
		{
			if (_context != (StoreContext)null)
			{
				_context.OfflineLicensesChanged -= OnOfflineLicensesChanged;
			}
		}
		finally
		{
			_context = null;
			Volatile.Write(ref _contextInitialized, 0);
		}
	}
}
