using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AirStereo2.ViewModels;

namespace AirStereo2.Services;

public static class HomePodConnectionNotice
{
	public const string DialogKey = "homepod-unpaired-stereo";

	public static SemaphoreSlim ConnectIntentGate { get; } = new SemaphoreSlim(1, 1);

	public static async Task<bool> ConfirmAsync(IEnumerable<DeviceViewModel> prospectiveDevices, IAppDialogService? dialogService = null)
	{
		HomePodAssessment homePodAssessment = HomePodStereoPolicy.Assess(prospectiveDevices);
		if (SilentStartupContext.SuppressUiDialogs || homePodAssessment.State != HomePodAssessmentState.ConfirmedIndependent)
		{
			return true;
		}
		LocalizationManager instance = LocalizationManager.Instance;
		return await (dialogService ?? AppDialogService.Instance).TryConfirmOnceAsync("homepod-unpaired-stereo", instance.GetString("HomePod_UnpairedStereoTitle", "HomePods are not a stereo pair"), instance.GetString("HomePod_UnpairedStereoMessage", "AirStereo2 sends the same audio to both HomePods; it does not separate left and right channels. For native left/right stereo, create a stereo pair in the Home app. Continue playing the same audio on these HomePods?"), instance.GetString("Dialog_OK", "OK"), instance.GetString("Dialog_Cancel", "Cancel")).ConfigureAwait(continueOnCapturedContext: true) == true;
	}
}
