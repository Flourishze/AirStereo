using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using AirStereo2.Messages;
using AirStereo2.ViewModels;

namespace CommunityToolkit.Mvvm.Messaging.__Internals;

[GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.IMessengerRegisterAllGenerator", "8.4.0.0")]
[DebuggerNonUserCode]
[ExcludeFromCodeCoverage]
[EditorBrowsable(EditorBrowsableState.Never)]
[Obsolete("This type is not intended to be used directly by user code")]
[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)]
internal static class __IMessengerExtensions
{
	[EditorBrowsable(EditorBrowsableState.Never)]
	[Obsolete("This method is not intended to be called directly by user code")]
	public static Action<IMessenger, object> CreateAllMessagesRegistrator(SettingsViewModel _)
	{
		return RegisterAll;
		static void RegisterAll(IMessenger messenger, object obj)
		{
			SettingsViewModel settingsViewModel = (SettingsViewModel)obj;
			IMessengerExtensions.Register<EqDeviceMemoryApplyMessage>(messenger, (IRecipient<EqDeviceMemoryApplyMessage>)(object)settingsViewModel);
		}
	}

	[EditorBrowsable(EditorBrowsableState.Never)]
	[Obsolete("This method is not intended to be called directly by user code")]
	public static Action<IMessenger, object, TToken> CreateAllMessagesRegistratorWithToken<TToken>(SettingsViewModel _) where TToken : IEquatable<TToken>
	{
		return RegisterAll;
		static void RegisterAll(IMessenger messenger, object obj, TToken token)
		{
			SettingsViewModel settingsViewModel = (SettingsViewModel)obj;
			IMessengerExtensions.Register<EqDeviceMemoryApplyMessage, TToken>(messenger, (IRecipient<EqDeviceMemoryApplyMessage>)(object)settingsViewModel, token);
		}
	}
}
