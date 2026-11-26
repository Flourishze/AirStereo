using System.Threading;
using System.Threading.Tasks;

namespace AirStereo2.Services;

public interface IAppDialogService
{
	Task ShowInfoAsync(string title, string message, CancellationToken cancellationToken = default(CancellationToken));

	Task ShowErrorAsync(string title, string message, CancellationToken cancellationToken = default(CancellationToken));

	Task ShowWarningAsync(string title, string message, CancellationToken cancellationToken = default(CancellationToken));

	Task<bool> ConfirmAsync(string title, string message, string? primaryText = null, string? closeText = null, bool isDestructive = false, CancellationToken cancellationToken = default(CancellationToken));

	Task<string?> PromptAsync(string title, string message, string defaultValue = "", string? primaryText = null, string? closeText = null, int maxLength = 0, CancellationToken cancellationToken = default(CancellationToken));

	Task<bool?> TryConfirmOnceAsync(string dedupeKey, string title, string message, string? primaryText = null, string? closeText = null, CancellationToken cancellationToken = default(CancellationToken));
}
