﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.ObjectModel;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Services.Cortana;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Windows.Devices.Radios;
using Windows.Devices.Bluetooth;
using Windows.UI.Xaml.Controls.Primitives;
using System.Linq;
using Windows.Foundation.Metadata;
using System.Threading;
using Windows.System;

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=234238

namespace IoTCoreDefaultApp
{
    // Simple collection of UI elements and data associated with Bluetooth Pairing
    public sealed class PairingContext
    {
        public PairingContext(Button yesButton, Button noButton, TextBlock confirmationText)
        {
            m_yesButton = yesButton;
            m_noButton = noButton;
            m_confirmationText = confirmationText;
        }

        // Rejects pairing request maintained by this pairing context
        public void RejectPairing()
        {
            if (m_deferral != null)
            {
                //Complete the deferral
                CompleteDeferral();
            }
        }

        public void CompleteDeferral()
        {
            // Complete the deferral
            if (m_deferral != null)
            {
                m_deferral.Complete();
                m_deferral = null;
            }
        }

        /// <summary>
        /// Accept the pairing and complete the deferral
        /// </summary>
        public void AcceptPairing()
        {
            if (m_pairingRequestedEventArgs != null)
            {
                m_pairingRequestedEventArgs.Accept();
            }
            // Complete deferral
            CompleteDeferral();
            m_pairingRequestedEventArgs = null;
        }

        public void AcceptPairingWithPIN(string PIN)
        {
            if (m_pairingRequestedEventArgs != null)
            {
                m_pairingRequestedEventArgs.Accept(PIN);
                m_pairingRequestedEventArgs = null;
            }
            // Complete the deferral here
            CompleteDeferral();
        }

        public void SetPairingRequestedEventArgs(DevicePairingRequestedEventArgs pairingRequestedEventArgs)
        {
            m_pairingRequestedEventArgs = pairingRequestedEventArgs;
            m_deferral = pairingRequestedEventArgs.GetDeferral();
        }

        public readonly Button m_yesButton;
        public readonly Button m_noButton;
        public readonly TextBlock m_confirmationText;
        private DevicePairingRequestedEventArgs m_pairingRequestedEventArgs = null;
        private Deferral m_deferral = null;
    };

    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class Settings : Page
    {
        private LanguageManager languageManager;
        // Device watcher
        private DeviceWatcher deviceWatcher = null;
        private TypedEventHandler<DeviceWatcher, DeviceInformation> handlerAdded = null;
        private TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> handlerUpdated = null;
        private TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> handlerRemoved = null;

        // Pairing controls and notifications
        private enum MessageType { YesNoMessage, OKMessage, InformationalMessage };
        Windows.Devices.Bluetooth.Rfcomm.RfcommServiceProvider provider = null; // To be used for inbound
        private string bluetoothConfirmOnlyFormatString;
        private string bluetoothDisplayPinFormatString;
        private string bluetoothConfirmPinMatchFormatString;
        private Windows.UI.Xaml.Controls.Button inProgressPairButton;
        Windows.UI.Xaml.Controls.Primitives.FlyoutBase savedPairButtonFlyout;

        private bool needsCortanaConsent = false;
        private bool cortanaConsentRequestedFromSwitch = false;

     
        // Inbound pairing related cache  (Pairing from an external device to this device)
        private PairingContext inboundContext;

        // Outbound pairing related cache (Pairing from this device to an external device)
        private PairingContext outboundContext;

        // Indicates whether or not inbound pairing is in progress
        private bool inboundPairingInProgress = false;

        static public ObservableCollection<BluetoothDeviceInformationDisplay> bluetoothDeviceObservableCollection
        {
            get;
            private set;
        } = new ObservableCollection<BluetoothDeviceInformationDisplay>();

        public Settings()
        {
            this.InitializeComponent();

            PreferencesListView.IsSelected = true;

            this.NavigationCacheMode = Windows.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
 
            this.DataContext = LanguageManager.GetInstance();

            this.Loaded += async (sender, e) =>
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                {
                    SetupLanguages();
                    SetupTimeZones();
                    screensaverToggleSwitch.IsOn = Screensaver.IsScreensaverEnabled;
                });
            };

            Window.Current.Activated += Window_Activated;

            inboundContext = new PairingContext(yesButtonInbound, noButtonInbound, confirmationTextInbound);
            outboundContext = new PairingContext(yesButtonOutbound, noButtonOutbound, confirmationTextOutbound);
        }

        private void SetupLanguages()
        {
            languageManager = LanguageManager.GetInstance();

            LanguageComboBox.ItemsSource = languageManager.LanguageDisplayNames;
            LanguageComboBox.SelectedItem = LanguageManager.GetCurrentLanguageDisplayName();

            InputLanguageComboBox.ItemsSource = languageManager.InputLanguageDisplayNames;
            InputLanguageComboBox.SelectedItem = LanguageManager.GetCurrentInputLanguageDisplayName();
            LangApplyStack.Visibility = Common.LangApplyRebootRequired ? Visibility.Visible : Visibility.Collapsed;
            if (Common.LangApplyRebootRequired)
            {
                if (!CortanaHelper.IsCortanaSupportedLanguage(languageManager.GetLanguageTagFromDisplayName(LanguageComboBox.SelectedItem as string)))
                {
                    LangApplyStack.Visibility = Visibility.Visible;
                }
            }
        }

        private void SetupTimeZones()
        {
            TimeZoneComboBox.ItemsSource = TimeZoneSettings.SupportedTimeZoneDisplayNames;
            TimeZoneComboBox.SelectedItem = TimeZoneSettings.CurrentTimeZoneDisplayName;
        }

        private void SetupBluetooth()
        {
            bluetoothDeviceListView.ItemsSource = bluetoothDeviceObservableCollection;
            RegisterForInboundPairingRequests();
        }

        private void SetupCortana()
        {
            var isCortanaSupported = CortanaHelper.IsCortanaSupported();
            
            cortanaConsentRequestedFromSwitch = false;

            // Only allow the Cortana settings to be enabled if Cortana is available on this device
            CortanaVoiceActivationSwitch.IsEnabled = isCortanaSupported;
            CortanaAboutMeButton.IsEnabled = isCortanaSupported;

            // If Cortana is supported on this device and the user has never granted voice consent,
            // then set a flag so that each time this page is activated we will poll for
            // Cortana's Global Consent Value and update the UI if needed.
            if (isCortanaSupported)
            {
                var cortanaSettings = CortanaSettings.GetDefault();
                needsCortanaConsent = !cortanaSettings.HasUserConsentToVoiceActivation;

                // If consent isn't needed, then update the voice activation switch to reflect its current system state.
                if (!needsCortanaConsent)
                {
                    CortanaVoiceActivationSwitch.IsOn = cortanaSettings.IsVoiceActivationEnabled;
                }
            }
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            // Resource loading has to happen on the UI thread
            bluetoothConfirmOnlyFormatString = Common.GetResourceText("BluetoothConfirmOnlyFormat");
            bluetoothDisplayPinFormatString = Common.GetResourceText("BluetoothDisplayPinFormat");
            bluetoothConfirmPinMatchFormatString = Common.GetResourceText("BluetoothConfirmPinMatchFormat");
            // Handle inbound pairing requests
            App.InboundPairingRequested += App_InboundPairingRequested;

            object oToggleSwitch = this.FindName("BluetoothToggle");
            if (oToggleSwitch != null)
            {
                var watcherToggle = oToggleSwitch as ToggleSwitch;
                if (watcherToggle.IsOn)
                {
                    if (deviceWatcher == null || (DeviceWatcherStatus.Stopped == deviceWatcher.Status))
                    {
                        StartWatchingAndDisplayConfirmationMessage();
                    }
                }
            }
            
            //Direct Jumping to Specific ListView from Outside
            if (null == e || null == e.Parameter)
            {
                await SwitchToSelectedSettingsAsync("PreferencesListViewItem");
                PreferencesListView.IsSelected = true;
            }
            else
            {
                await SwitchToSelectedSettingsAsync(e.Parameter.ToString());
            }

        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            // Reject any pairing that might be in progress
            inboundContext.RejectPairing();
            outboundContext.RejectPairing();

            // Clear the confirmation message
            ClearConfirmationPanel(inboundContext);
            ClearConfirmationPanel(outboundContext);

            StopWatcher();
            inboundPairingInProgress = false;
        }

        public void ClearConfirmationPanel(PairingContext pairingContext)
        {
            pairingContext.m_confirmationText.Text = "";
            pairingContext.m_yesButton.Visibility = Visibility.Collapsed;
            pairingContext.m_noButton.Visibility = Visibility.Collapsed;           
        }

        private async void App_InboundPairingRequested(object sender, InboundPairingEventArgs inboundArgs)
        {
            // Note: This demo only supports a single inbound pairing operation at a time.  This discards additional pairing requests.
            // This also discards multiple pairing requests from the same device
            if (inboundPairingInProgress)
            {
                return;
            }
            inboundPairingInProgress = true;

            await MainPage.Current.UIThreadDispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                // Make sure the Bluetooth grid is showing
                await SwitchToSelectedSettingsAsync("BluetoothListViewItem");

                BluetoothDeviceInformationDisplay deviceInfoDisp = new BluetoothDeviceInformationDisplay(inboundArgs.DeviceInfo);

                // Display a message about the inbound request
                string formatString = Common.GetResourceText("BluetoothInboundPairingRequestFormat");
                string confirmationMessage = string.Format(formatString, deviceInfoDisp.Name, deviceInfoDisp.Id);
                DisplayMessagePanelAsync(inboundContext, confirmationMessage, MessageType.InformationalMessage);

                DevicePairingKinds supportedCeremonies = DevicePairingKinds.DisplayPin | DevicePairingKinds.ConfirmPinMatch | DevicePairingKinds.ConfirmOnly;

                inboundArgs.DeviceInfo.Pairing.Custom.PairingRequested += InboundPairingRequestedHandler;
                var result = await inboundArgs.DeviceInfo.Pairing.Custom.PairAsync(supportedCeremonies);
                inboundArgs.DeviceInfo.Pairing.Custom.PairingRequested -= InboundPairingRequestedHandler;

            });
            inboundPairingInProgress = false;
        }

        private void StartWatchingAndDisplayConfirmationMessage()
        {
            // Clear the current collection
            bluetoothDeviceObservableCollection.Clear();
            // Start the watcher
            if (StartWatcher())
            {
                // Display a message
                 bluetoothMessageText.Text = Common.GetResourceText("BluetoothOn");
            }
        }

        private void BackButton_Clicked(object sender, RoutedEventArgs e)
        {
            NavigationUtils.GoBack();
        }

        private void LanguageComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (ApiInformation.IsPropertyPresent("Windows.UI.Xaml.Controls.ComboBox", "IsTextSearchEnabled"))
            {
                LanguageComboBox.IsTextSearchEnabled = true;
            }

            if (ApiInformation.IsPropertyPresent("Windows.UI.Xaml.Controls.ComboBox", "LightDismissOverlayMode"))
            {
                LanguageComboBox.LightDismissOverlayMode = LightDismissOverlayMode.On;
            }
        }

        private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var comboBox = sender as ComboBox;
            if (comboBox.SelectedItem == null)
            {
                return;
            }

            //Check existing lang Tuple
            var currentLangTuple = languageManager.GetLanguageTuple(languageManager.GetLanguageTagFromDisplayName(comboBox.SelectedItem as string));
            SpeechSupport.Text = currentLangTuple.Item2 ? languageManager["SpeechSupportText"] : languageManager["SpeechNotSupportText"];

            //Check the Primary Override and Lang Selected
            if (LanguageManager.GetCurrentLanguageDisplayName().Equals(comboBox.SelectedItem as string))
            {
                //Do Nothing
                return;
            }

            //Reset
            Common.LangApplyRebootRequired = false;
            LangApplyStack.Visibility = Visibility.Collapsed;


            //No action if already selected is same as device lang or proposed lang is same as current
            if (LanguageManager.GetCurrentLanguageDisplayName().Equals(comboBox.SelectedItem as string))
            {
                //Do Nothing
                return;
            }

            //Check if selected language is part of ffu
            var newLang = languageManager.CheckUpdateLanguage(comboBox.SelectedItem as string);

            //Selected Language, but Check Proposing other language
            if (LanguageManager.GetDisplayNameFromLanguageTag(newLang.Item4).Equals(comboBox.SelectedItem as string))
            {
                //Update
                var langReturned = languageManager.UpdateLanguage(comboBox.SelectedItem as string);

                //ffu list, Show user to restart to use the System Languages
                if (newLang.Item1)
                {
                    Common.LangApplyRebootRequired = true;
                    LangApplyStack.Visibility = Visibility.Visible;
                }
                //else
                //skip providing option to restart app
            }
            else
            {

                //Check if the backup language support speech, else dont even show the popup
                //Speech Supports
                if (newLang.Item2)
                {
                    //If different, show the popup for confirmation
                    PopupText2.Text = LanguageManager.GetDisplayNameFromLanguageTag(newLang.Item4);
                    PopupYes.Content = LanguageManager.GetDisplayNameFromLanguageTag(newLang.Item4);

                    PopupNo.Content = comboBox.SelectedItem as string;

                    double hOffset = (Window.Current.Bounds.Width) / 4;
                    double vOffset = (Window.Current.Bounds.Height) / 2;

                    StandardPopup.VerticalOffset = vOffset;
                    StandardPopup.HorizontalOffset = hOffset;

                    if (!StandardPopup.IsOpen) { StandardPopup.IsOpen = true; }
                }
                else
                {
                    //Just update silently in the background and dont ask for restart app
                    var langReturned = languageManager.UpdateLanguage(comboBox.SelectedItem as string);

                }
            }

        }

        // Handles the Click event on the Button inside the Popup control
        private void PopupYes_Clicked(object sender, RoutedEventArgs e)
        {
            var curLang = LanguageManager.GetCurrentLanguageDisplayName();
            if (curLang.Equals(PopupYes.Content as string))
            {
                Common.LangApplyRebootRequired = false;
                LangApplyStack.Visibility = Visibility.Collapsed;

            }
            else
            {
                //Update
                var langReturned = languageManager.UpdateLanguage(PopupYes.Content as string, true);

                Common.LangApplyRebootRequired = true;
                LangApplyStack.Visibility = Visibility.Visible;
            }

            LanguageComboBox.SelectedItem = LanguageManager.GetCurrentLanguageDisplayName();
            if (StandardPopup.IsOpen) { StandardPopup.IsOpen = false; }

            //Check existing lang Tuple
            var currentLangTuple = languageManager.GetLanguageTuple(languageManager.GetLanguageTagFromDisplayName(LanguageComboBox.SelectedItem as string));
            SpeechSupport.Text = currentLangTuple.Item2 ? languageManager["SpeechSupportText"] : languageManager["SpeechNotSupportText"];

        }

        private void PopupNo_Clicked(object sender, RoutedEventArgs e)
        {
            var curLang = LanguageManager.GetCurrentLanguageDisplayName();

            if (curLang.Equals(PopupNo.Content as string))
            {
                Common.LangApplyRebootRequired = false;
                LangApplyStack.Visibility = Visibility.Collapsed;

            }
            else
            {
                var langReturned = languageManager.UpdateLanguage(PopupNo.Content as string, false);
                Common.LangApplyRebootRequired = true;
                LangApplyStack.Visibility = Visibility.Visible;

            }

            LanguageComboBox.SelectedItem = LanguageManager.GetCurrentLanguageDisplayName();
            if (StandardPopup.IsOpen) { StandardPopup.IsOpen = false; }

            //Check existing lang Tuple
            var currentLangTuple = languageManager.GetLanguageTuple(languageManager.GetLanguageTagFromDisplayName(LanguageComboBox.SelectedItem as string));
            SpeechSupport.Text = currentLangTuple.Item2 ? languageManager["SpeechSupportText"] : languageManager["SpeechNotSupportText"];

            //Enable only if selected lang supports speech or image localization
            if (currentLangTuple.Item1 || currentLangTuple.Item2)
            {
                Common.LangApplyRebootRequired = true;
                LangApplyStack.Visibility = Visibility.Visible;
            }

        }

        private void LangApplyYes_Click(object sender, RoutedEventArgs e)
        {
            //Restart app
            Windows.ApplicationModel.Core.CoreApplication.Exit();
        }

        private void InputLanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var comboBox = sender as ComboBox;
            if (comboBox.SelectedItem == null)
            {
                return;
            }

            languageManager.UpdateInputLanguage(comboBox.SelectedItem as string);
        }


        private async void SettingsChoice_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as FrameworkElement;
            if (item == null)
            {
                return;
            }

            // Language, Network, or Bluetooth settings etc.
            await SwitchToSelectedSettingsAsync(item.Name);
        }

        /// <summary>
        /// Helps Hiding all other Grid Views except the selected Grid
        /// </summary>
        /// <param name="itemName"></param>
        private async Task SwitchToSelectedSettingsAsync(string itemName)
        {
            switch (itemName)
            {
                case "PreferencesListViewItem":
                    NetworkControl.Visibility = Visibility.Collapsed;
                    BluetoothGrid.Visibility = Visibility.Collapsed;
                    CortanaGrid.Visibility = Visibility.Collapsed;

                    if (BasicPreferencesGridView.Visibility == Visibility.Collapsed)
                    {
                        BasicPreferencesGridView.Visibility = Visibility.Visible;
                        PreferencesListView.IsSelected = true;
                    }
                    break;
                case "NetworkListViewItem":
                    BasicPreferencesGridView.Visibility = Visibility.Collapsed;
                    BluetoothGrid.Visibility = Visibility.Collapsed;
                    CortanaGrid.Visibility = Visibility.Collapsed;

                    if (NetworkControl.Visibility == Visibility.Collapsed)
                    {
                        NetworkControl.Visibility = Visibility.Visible;
                        NetworkListView.IsSelected = true;
                        NetworkControl.SetupDirectConnection();
                        await NetworkControl.RefreshWifiListViewItemsAsync(true);
                    }
                    break;
                case "BluetoothListViewItem":
                    BasicPreferencesGridView.Visibility = Visibility.Collapsed;
                    NetworkControl.Visibility = Visibility.Collapsed;
                    CortanaGrid.Visibility = Visibility.Collapsed;

                    if (BluetoothGrid.Visibility == Visibility.Collapsed)
                    {
                        BluetoothGrid.Visibility = Visibility.Visible;
                        BluetoothListView.IsSelected = true;
                        if (await IsBluetoothEnabledAsync())
                        {
                            BluetoothToggle.IsOn = true;
                        }
                        else
                        {
                            TurnOffBluetooth();
                        }
                    }
                    break;
                case "CortanaListViewItem":
                    BasicPreferencesGridView.Visibility = Visibility.Collapsed;
                    NetworkControl.Visibility = Visibility.Collapsed;
                    BluetoothGrid.Visibility = Visibility.Collapsed;

                    if (CortanaGrid.Visibility == Visibility.Collapsed)
                    {
                        SetupCortana();
                        CortanaGrid.Visibility = Visibility.Visible;
                        CortanaListView.IsSelected = true;
                    }
                    break;
                default:
                    break;

            }
        }

        /// <summary>
        /// Start the Device Watcher and set callbacks to handle devices appearing and disappearing
        /// </summary>
        private bool StartWatcher()
        {
            try
            {
                //ProtocolSelectorInfo protocolSelectorInfo;
                string aqsFilter = @"System.Devices.Aep.ProtocolId:=""{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}"" OR System.Devices.Aep.ProtocolId:=""{bb7bb05e-5972-42b5-94fc-76eaa7084d49}""";  //Bluetooth + BluetoothLE

                // Request the IsPaired property so we can display the paired status in the UI
                string[] requestedProperties = { "System.Devices.Aep.IsPaired" };

                //// Get the device selector chosen by the UI, then 'AND' it with the 'CanPair' property
                //protocolSelectorInfo = (ProtocolSelectorInfo)selectorComboBox.SelectedItem;
                //aqsFilter = protocolSelectorInfo.Selector + " AND System.Devices.Aep.CanPair:=System.StructuredQueryType.Boolean#True";

                deviceWatcher = DeviceInformation.CreateWatcher(
                    aqsFilter,
                    requestedProperties,
                    DeviceInformationKind.AssociationEndpoint
                    );

                // Hook up handlers for the watcher events before starting the watcher

                handlerAdded = new TypedEventHandler<DeviceWatcher, DeviceInformation>(async (watcher, deviceInfo) =>
                {
                // Since we have the collection databound to a UI element, we need to update the collection on the UI thread.
                await MainPage.Current.UIThreadDispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                    {
                        bluetoothDeviceObservableCollection.Add(new BluetoothDeviceInformationDisplay(deviceInfo));
                    });
                });
                deviceWatcher.Added += handlerAdded;

                handlerUpdated = new TypedEventHandler<DeviceWatcher, DeviceInformationUpdate>(async (watcher, deviceInfoUpdate) =>
                {
                // Since we have the collection databound to a UI element, we need to update the collection on the UI thread.
                await MainPage.Current.UIThreadDispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                    {
                    // Find the corresponding updated DeviceInformation in the collection and pass the update object
                    // to the Update method of the existing DeviceInformation. This automatically updates the object
                    // for us.
                    foreach (BluetoothDeviceInformationDisplay deviceInfoDisp in bluetoothDeviceObservableCollection)
                        {
                            if (deviceInfoDisp.Id == deviceInfoUpdate.Id)
                            {
                                deviceInfoDisp.Update(deviceInfoUpdate);
                                break;
                            }
                        }
                    });
                });
                deviceWatcher.Updated += handlerUpdated;

                handlerRemoved = new TypedEventHandler<DeviceWatcher, DeviceInformationUpdate>(async (watcher, deviceInfoUpdate) =>
                {
                // Since we have the collection databound to a UI element, we need to update the collection on the UI thread.
                await MainPage.Current.UIThreadDispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                    {
                    // Find the corresponding DeviceInformation in the collection and remove it
                    foreach (BluetoothDeviceInformationDisplay deviceInfoDisp in bluetoothDeviceObservableCollection)
                        {
                            if (deviceInfoDisp.Id == deviceInfoUpdate.Id)
                            {
                                bluetoothDeviceObservableCollection.Remove(deviceInfoDisp);
                                break;
                            }
                        }
                    });
                });
                deviceWatcher.Removed += handlerRemoved;

                // Start the Device Watcher
                deviceWatcher.Start();
            }
            catch (Exception e)
            {
                string formatString = Common.GetResourceText("BluetoothListenerCreationFailedFormat");
                string confirmationMessage = string.Format(formatString, e.Message);
                bluetoothMessageText.Text = confirmationMessage;
                deviceWatcher = null;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Stop the Device Watcher
        /// </summary>
        private void StopWatcher()
        {
            if (null != deviceWatcher)
            {
                // First unhook all event handlers except the stopped handler. This ensures our
                // event handlers don't get called after stop, as stop won't block for any "in flight" 
                // event handler calls.  We leave the stopped handler as it's guaranteed to only be called
                // once and we'll use it to know when the query is completely stopped. 
                deviceWatcher.Added -= handlerAdded;
                deviceWatcher.Updated -= handlerUpdated;
                deviceWatcher.Removed -= handlerRemoved;

                if (DeviceWatcherStatus.Started == deviceWatcher.Status ||
                    DeviceWatcherStatus.EnumerationCompleted == deviceWatcher.Status)
                {
                    deviceWatcher.Stop();
                }
            }
        }

        private async void DisplayMessagePanelAsync(PairingContext pairingContext, string confirmationMessage, MessageType messageType, bool force = false)
        {
            // Use UI thread
            await MainPage.Current.UIThreadDispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                Button yesButton = pairingContext.m_yesButton;
                Button noButton = pairingContext.m_noButton;
                TextBlock confirmationMessageText = pairingContext.m_confirmationText;

                if (!force && !BluetoothToggle.IsOn)
                {
                    ClearConfirmationPanel(pairingContext);
                }

                pairingContext.m_confirmationText.Text = confirmationMessage;
                if (messageType == MessageType.OKMessage)
                {
                    yesButton.Content = Common.GetResourceText("OKLabel");
                    yesButton.Visibility = Visibility.Visible;
                    noButton.Visibility = Visibility.Collapsed;
                }
                else if (messageType == MessageType.InformationalMessage)
                {
                    // Just make the buttons invisible
                    yesButton.Visibility = Visibility.Collapsed;
                    noButton.Visibility = Visibility.Collapsed;
                }
                else
                {
                    yesButton.Content = Common.GetResourceText("YesLabel");
                    yesButton.Visibility = Visibility.Visible;
                    noButton.Visibility = Visibility.Visible;
                }
            });
        }
  
        /// <summary>
        /// The Yes or OK button on the DisplayConfirmationPanelAndComplete - accepts the pairing, completes the deferral and clears the message panel
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void YesButton_Click(object sender, RoutedEventArgs e)
        {
            var pairingContext = (sender == yesButtonInbound) ? inboundContext : outboundContext;

            // Accept the pairing
            pairingContext.AcceptPairing();

            //Display InProgress Status while in pairing
            DisplayMessagePanelAsync(pairingContext, Common.GetResourceText("BluetoothPairingRequestProgress"), MessageType.InformationalMessage);
        }
      
        /// <summary>
        /// The No button on the DisplayConfirmationPanelAndComplete - completes the deferral and clears the message panel
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void NoButton_Click(object sender, RoutedEventArgs e)
        {
            var pairingContext = (sender == noButtonInbound) ? inboundContext : outboundContext;
            pairingContext.RejectPairing();
            ClearConfirmationPanel(pairingContext);
        }
     

        /// <summary>
        /// User wants to use custom pairing with the selected ceremony types and Default protection level
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void PairButton_Click(object sender, RoutedEventArgs e)
        {
            // Use the pair button on the bluetoothDeviceListView.SelectedItem to get the data context
            BluetoothDeviceInformationDisplay deviceInfoDisp =
                ((Button)sender).DataContext as BluetoothDeviceInformationDisplay;
            string formatString = Common.GetResourceText("BluetoothAttemptingToPairFormat");
            string confirmationMessage = string.Format(formatString, deviceInfoDisp.Name, deviceInfoDisp.Id);
            DisplayMessagePanelAsync(outboundContext, confirmationMessage, MessageType.InformationalMessage);

            // Save the pair button
            Button pairButton = sender as Button;
            inProgressPairButton = pairButton;

            // Save the flyout and set to null so it doesn't pop up unless we want it
            savedPairButtonFlyout = pairButton.Flyout;
            inProgressPairButton.Flyout = null;

            // Disable the pair button until we are done
            pairButton.IsEnabled = false;

            // Get ceremony type and protection level selections
            DevicePairingKinds ceremoniesSelected = GetSelectedCeremonies();
            // Get protection level
            DevicePairingProtectionLevel protectionLevel = DevicePairingProtectionLevel.Default;

            // Specify custom pairing with all ceremony types and protection level EncryptionAndAuthentication
            DeviceInformationCustomPairing customPairing = deviceInfoDisp.DeviceInformation.Pairing.Custom;

            customPairing.PairingRequested += OutboundPairingRequestedHandler;
            DevicePairingResult result = await customPairing.PairAsync(ceremoniesSelected, protectionLevel);
            customPairing.PairingRequested -= OutboundPairingRequestedHandler;

            if (result.Status == DevicePairingResultStatus.Paired)
            {
                formatString = Common.GetResourceText("BluetoothPairingSuccessFormat");
                confirmationMessage = string.Format(formatString, deviceInfoDisp.Name, deviceInfoDisp.Id);
            }
            else
            {
                formatString = Common.GetResourceText("BluetoothPairingFailureFormat");
                confirmationMessage = string.Format(formatString, result.Status.ToString(), deviceInfoDisp.Name,
                    deviceInfoDisp.Id);
            }
            // Display the result of the pairing attempt
            DisplayMessagePanelAsync(outboundContext, confirmationMessage, MessageType.InformationalMessage);

            // If the watcher toggle is on, clear any devices in the list and stop and restart the watcher to ensure state is reflected in list
            if (BluetoothToggle.IsOn)
            {
                bluetoothDeviceObservableCollection.Clear();
                StopWatcher();
                StartWatcher();
            }

            // Re-enable the pair button
            inProgressPairButton = null;
            pairButton.IsEnabled = true;
        }

        /// <summary>
        /// Called when custom pairing is initiated so that we can handle the custom ceremony
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private void InboundPairingRequestedHandler(
            DeviceInformationCustomPairing sender,
            DevicePairingRequestedEventArgs args)
        {
            PairingRequestedHandlerAsync(inboundContext, args).Wait();
        }

        /// <summary>
        /// Called when custom pairing is initiated so that we can handle the custom ceremony
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private void OutboundPairingRequestedHandler(
            DeviceInformationCustomPairing sender,
            DevicePairingRequestedEventArgs args)
        {
            PairingRequestedHandlerAsync(outboundContext, args).Wait();
        }


        /// <summary>
        /// Called when custom pairing is initiated so that we can handle the custom ceremony
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private async Task PairingRequestedHandlerAsync(
            PairingContext pairingContext,
            DevicePairingRequestedEventArgs args)
        {
            //Null Check
            if (null == args || null == pairingContext)
                return;

            // Save the args for use in ProvidePin case
            pairingContext.SetPairingRequestedEventArgs(args);

            string confirmationMessage;

            switch (args.PairingKind)
            {
                case DevicePairingKinds.ConfirmOnly:
                    {
                        confirmationMessage = string.Format(bluetoothConfirmOnlyFormatString,
                            null != args.DeviceInformation ? args.DeviceInformation.Name : string.Empty,
                            null != args.DeviceInformation ? args.DeviceInformation.Id : string.Empty);
                        DisplayMessagePanelAsync(pairingContext,confirmationMessage, MessageType.YesNoMessage);
                    }
                    break;

                case DevicePairingKinds.DisplayPin:
                    // We just show the PIN on this side. The ceremony is actually completed when the user enters the PIN
                    // on the target device
                    {
                        confirmationMessage = string.Format(bluetoothDisplayPinFormatString, args.Pin);
                        DisplayMessagePanelAsync(pairingContext, confirmationMessage, MessageType.InformationalMessage);
                        pairingContext.CompleteDeferral();
                    }
                    break;

                case DevicePairingKinds.ProvidePin:
                    // A PIN may be displayed on the target device, or is a hardcoded value on the target device.
                    // The user needs to enter the matching PIN on this Windows device.
                    if (pairingContext == outboundContext)
                    {
                        await MainPage.Current.UIThreadDispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
                        {
                            // PIN Entry
                            inProgressPairButton.Flyout = savedPairButtonFlyout;
                            inProgressPairButton.Flyout.ShowAt(inProgressPairButton);
                        });
                    }
                    break;

                case DevicePairingKinds.ConfirmPinMatch:
                    // We show the PIN here and the user responds with whether the PIN matches what they see
                    // on the target device. Response comes back and we set it on the PinComparePairingRequestedData
                    // then complete the deferral.
                    {
                        confirmationMessage = string.Format(bluetoothConfirmPinMatchFormatString, args.Pin);
                        DisplayMessagePanelAsync(pairingContext, confirmationMessage, MessageType.YesNoMessage);
                    }
                    break;
            }
        }

        /// <summary>
        /// Turn on Bluetooth Radio and list available Bluetooth Devices
        /// </summary>
        private async void TurnOnRadio()
        {
            // Display a message
            bluetoothMessageText.Text = Common.GetResourceText("BluetoothOn");
            ClearConfirmationPanel(inboundContext);
            ClearConfirmationPanel(outboundContext);

            await ToggleBluetoothAsync(true);
            SetupBluetooth();
        }

        /// <summary>
        /// Checks the state of Bluetooth Radio 
        /// </summary>
        private async Task<bool> IsBluetoothEnabledAsync()
        {
            var radios = await Radio.GetRadiosAsync();
            var bluetoothRadio = radios.FirstOrDefault(radio => radio.Kind == RadioKind.Bluetooth);
            return bluetoothRadio != null && bluetoothRadio.State == RadioState.On;
        }

        private async Task ToggleBluetoothAsync(bool bluetoothState)
        {
            try
            {
                var access = await Radio.RequestAccessAsync();
                if (access != RadioAccessStatus.Allowed)
                {
                    return;
                }
                BluetoothAdapter adapter = await BluetoothAdapter.GetDefaultAsync();
                if (null != adapter)
                {
                    var btRadio = await adapter.GetRadioAsync();
                    if (bluetoothState)
                    {
                        await btRadio.SetStateAsync(RadioState.On);
                        StartWatchingAndDisplayConfirmationMessage();
                    }
                    else
                    {
                        await btRadio.SetStateAsync(RadioState.Off);
                    }
                }
                else
                {
                    if (bluetoothState)
                    {
                        NoDeviceFound();
                    }
                }

            }
            catch (Exception e)
            {
                NoDeviceFound(e.Message);
            }
        }


        private void NoDeviceFound(string message = "")
        {
            string formatString = Common.GetResourceText("BluetoothNoDeviceAvailableFormat");
            string confirmationMessage = string.Format(formatString, message);
            DisplayMessagePanelAsync(outboundContext, confirmationMessage, MessageType.InformationalMessage);
        }

        /// <summary>
        /// Turn off Bluetooth Radio and stops watching for Bluetooth devices
        /// </summary>
        private async void TurnOffBluetooth()
        {
            // Clear any devices in the list
            bluetoothDeviceObservableCollection.Clear();
            // Stop the watcher
            //Check StopWatcher();

            // Display a message
            bluetoothMessageText.Text = Common.GetResourceText("BluetoothOff");
            
            ClearConfirmationPanel(inboundContext);
            ClearConfirmationPanel(outboundContext);

            await ToggleBluetoothAsync(false);
        }

        /// <summary>
        /// User wants to unpair from the selected device
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void UnpairButton_Click(object sender, RoutedEventArgs e)
        {
            // Use the unpair button on the bluetoothDeviceListView.SelectedItem to get the data context
            BluetoothDeviceInformationDisplay deviceInfoDisp = ((Button)sender).DataContext as BluetoothDeviceInformationDisplay;
            string formatString;
            string confirmationMessage;

            Button unpairButton = sender as Button;
            // Disable the unpair button until we are done
            unpairButton.IsEnabled = false;

            DeviceUnpairingResult unpairingResult = await deviceInfoDisp.DeviceInformation.Pairing.UnpairAsync();

            if (unpairingResult.Status == DeviceUnpairingResultStatus.Unpaired)
            {
                // Device is unpaired
                formatString = Common.GetResourceText("BluetoothUnpairingSuccessFormat");
                confirmationMessage = string.Format(formatString, deviceInfoDisp.Name, deviceInfoDisp.Id);
            }
            else
            {
                formatString = Common.GetResourceText("BluetoothUnpairingFailureFormat");
                confirmationMessage = string.Format(formatString, unpairingResult.Status.ToString(), deviceInfoDisp.Name, deviceInfoDisp.Id);
            }
            // Display the result of the pairing attempt
            DisplayMessagePanelAsync(outboundContext, confirmationMessage, MessageType.InformationalMessage);

            // If the watcher toggle is on, clear any devices in the list and stop and restart the watcher to ensure state is reflected in list
            if (BluetoothToggle.IsOn)
            {
                bluetoothDeviceObservableCollection.Clear();
                StopWatcher();
                StartWatcher();
            }
            else
            {
                // If the watcher is off this is an inbound request so just clear the list
                bluetoothDeviceObservableCollection.Clear();
            }

            // Re-enable the unpair button
            unpairButton.IsEnabled = true;
        }

        /// <summary>
        /// User has entered a PIN and pressed <Return> in the PIN entry flyout
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void PinEntryTextBox_KeyDown(object sender, Windows.UI.Xaml.Input.KeyRoutedEventArgs e)
        {            
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                //  Close the flyout and save the PIN the user entered
                TextBox bluetoothPINTextBox = sender as TextBox;
                string pairingPIN = bluetoothPINTextBox.Text;
                if (pairingPIN != "")
                {
                    // Hide the flyout
                    inProgressPairButton.Flyout.Hide();
                    inProgressPairButton.Flyout = null;
                    // Use the PIN to accept the pairing
                    outboundContext.AcceptPairingWithPIN(pairingPIN);
                }
            }
        }      

        /// <summary>
        /// Call when selection changes on the list of discovered Bluetooth devices
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ResultsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {

        }

        /// <summary>
        /// Get the set of acceptable ceremonies from the check boxes
        /// </summary>
        /// <returns></returns>
        private DevicePairingKinds GetSelectedCeremonies()
        {
            DevicePairingKinds ceremonySelection = DevicePairingKinds.ConfirmOnly | DevicePairingKinds.DisplayPin | DevicePairingKinds.ProvidePin | DevicePairingKinds.ConfirmPinMatch;
            return ceremonySelection;
        }

        /// <summary>
        /// Set the check boxes to refelect the set of acceptable ceremonies
        /// </summary>
        /// <param name="selectedCeremonies"></param>
        private void SetSelectedCeremonies(int selectedCeremonies)
        {
            // Currently a no-op, but would be used if checkboxes are added to restrict ceremony types
        }

        private async void RegisterForInboundPairingRequests()
        {
            // Make the system discoverable for Bluetooth
            await MakeDiscoverable();

            // If the attempt to make the system discoverable failed then likely there is no Bluetooth device present
            // so leave the diagnositic message put out by the call to MakeDiscoverable()
            if (App.IsBluetoothDiscoverable)
            {
                string formatString;
                string confirmationMessage;

                // Get state of ceremony checkboxes
                DevicePairingKinds ceremoniesSelected = GetSelectedCeremonies();
                if (!DeviceInformationPairing.TryRegisterForAllInboundPairingRequests(ceremoniesSelected))
                {
                    confirmationMessage = Common.GetResourceText("BluetoothInboundRegistrationFailed");
                }
                else
                {
                    // Save off the ceremonies we registered with
                    formatString = Common.GetResourceText("BluetoothInboundRegistrationSucceededFormat");
                    confirmationMessage = string.Format(formatString, ceremoniesSelected.ToString());
                }
                
                // Display a message
                bluetoothMessageText.Text = Common.GetResourceText("BluetoothOn");
            }
        }

        private async System.Threading.Tasks.Task MakeDiscoverable()
        {
            // Make the system discoverable. Don'd repeatedly do this or the StartAdvertising will throw "cannot create a file when that file already exists"
            if (!App.IsBluetoothDiscoverable)
            {
                Guid BluetoothServiceUuid = new Guid("17890000-0068-0069-1532-1992D79BE4D8");
                try
                {
                    provider = await RfcommServiceProvider.CreateAsync(RfcommServiceId.FromUuid(BluetoothServiceUuid));
                    Windows.Networking.Sockets.StreamSocketListener listener = new Windows.Networking.Sockets.StreamSocketListener();
                    listener.ConnectionReceived += OnConnectionReceived;
                    await listener.BindServiceNameAsync(provider.ServiceId.AsString(), Windows.Networking.Sockets.SocketProtectionLevel.PlainSocket);
                    //     SocketProtectionLevel.BluetoothEncryptionAllowNullAuthentication);
                    // Don't bother setting SPD attributes
                    provider.StartAdvertising(listener, true);
                    App.IsBluetoothDiscoverable = true;
                }
                catch (Exception e)
                {
                    string formatString = Common.GetResourceText("BluetoothNoDeviceAvailableFormat");
                    string confirmationMessage = string.Format(formatString, e.Message);
                    DisplayMessagePanelAsync(inboundContext, confirmationMessage, MessageType.InformationalMessage);
                }
            }
        }

        /// <summary>
        /// We have to have a callback handler to handle "ConnectionReceived" but we don't do anything because
        /// the StartAdvertising is just a way to turn on Bluetooth discoverability
        /// </summary>
        /// <param name="listener"></param>
        /// <param name="args"></param>
        void OnConnectionReceived(Windows.Networking.Sockets.StreamSocketListener listener,
                                   Windows.Networking.Sockets.StreamSocketListenerConnectionReceivedEventArgs args)
        {
        }

        private void BluetoothToggle_Toggled(object sender, RoutedEventArgs e)
        {
            var bluetoothOnOffSwitch = sender as ToggleSwitch;
            if (bluetoothOnOffSwitch.IsOn)
            {
                TurnOnRadio();
            }
            else
            {
                TurnOffBluetooth();
            }
        }

        private void Screensaver_Toggled(object sender, RoutedEventArgs e)
        {
            var screensaverToggleSwitch = sender as ToggleSwitch;
            Screensaver.IsScreensaverEnabled = screensaverToggleSwitch.IsOn;
        }

        private async void CortanaVoiceActivationSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            var cortanaSettings = CortanaSettings.GetDefault();
            var cortanaVoiceActivationSwitch = (ToggleSwitch)sender;

            bool enableVoiceActivation = cortanaVoiceActivationSwitch.IsOn;

            // If user is requesting to turn on voice activation, but consent has not been provided yet, then launch Cortana to ask for consent first
            if (!cortanaSettings.HasUserConsentToVoiceActivation)
            {
                // Guard against the case where the switch is toggled off when Consent hasn't been given yet
                // This occurs when we are re-entering this method when the switch is turned off in the code that follows
                if (!enableVoiceActivation)
                {
                    return;
                }

                // Launch Cortana to get the User Consent.  This is required before a change to enable voice activation is permitted
                CortanaVoiceActivationSwitch.IsEnabled = false;
                needsCortanaConsent = true;
                CortanaVoiceActivationSwitch.IsOn = false;
                cortanaConsentRequestedFromSwitch = true;
                await CortanaHelper.LaunchCortanaToConsentPageAsync();
            }
            // Otherwise, we already have consent, so just enable or disable the voice activation setting.
            // Do this asynchronously because the API waits for the SpeechRuntime EXE to launch
            else
            {
                CortanaVoiceActivationSwitch.IsEnabled = false;
                await Window.Current.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                {
                    await SetVoiceActivation(enableVoiceActivation);
                    CortanaVoiceActivationSwitch.IsEnabled = true;
                });
            }
        }

        private async void Window_Activated(object sender, WindowActivatedEventArgs e)
        {
            switch (e.WindowActivationState)
            {
                case CoreWindowActivationState.PointerActivated:
                case CoreWindowActivationState.CodeActivated:
                    if (needsCortanaConsent)
                    {
                        // Re-enable the voice activation selection
                        CortanaVoiceActivationSwitch.IsEnabled = true;

                        // Verify whether consent has changed while the screen was away
                        var cortanaSettings = CortanaSettings.GetDefault();
                        if (cortanaSettings.HasUserConsentToVoiceActivation)
                        {
                            // Consent was granted, so finish the task of flipping the switch to the current activation-state
                            // (It is possible that Cortana Consent was granted by some other application, while
                            // the default app was running, but not by the user actively flipping the switch,
                            // so update the switch state to the current global setting)                           
                            if (cortanaConsentRequestedFromSwitch)
                            {
                                await SetVoiceActivation(true);
                                cortanaConsentRequestedFromSwitch = false;
                            }

                            // Set the switch to the current global state
                            CortanaVoiceActivationSwitch.IsOn = cortanaSettings.IsVoiceActivationEnabled;

                            // We no longer need consent
                            needsCortanaConsent = false;
                        }
                    }
                    break;

                default:
                    break;
            }
        }

        const int RPC_S_CALL_FAILED = -2147023170;
        const int RPC_S_SERVER_UNAVAILABLE = -2147023174;
        const int RPC_S_SERVER_TOO_BUSY = -2147023175;
        const int MAX_VOICEACTIVATION_TRIALS = 5;
        const int TIMEINTERVAL_VOICEACTIVATION = 10;    // milli sec
        private async Task SetVoiceActivation(bool value)
        {
            var cortanaSettings = CortanaSettings.GetDefault();
            for (int i = 0; i < MAX_VOICEACTIVATION_TRIALS; i++)
            {
                try
                {
                    cortanaSettings.IsVoiceActivationEnabled = value;
                }
                catch (System.Exception ex)
                {
                    if (ex.HResult == RPC_S_CALL_FAILED ||
                        ex.HResult == RPC_S_SERVER_UNAVAILABLE ||
                        ex.HResult == RPC_S_SERVER_TOO_BUSY)
                    {
                        // VoiceActivation server is very likely busy =>
                        // yield and take a new ref to CortanaSettings API
                        await Task.Delay(TimeSpan.FromMilliseconds(TIMEINTERVAL_VOICEACTIVATION));
                        cortanaSettings = CortanaSettings.GetDefault();
                    }
                    else
                    {
                        throw ex;
                    }
                }
            }
        }

        private async void CortanaAboutMeButton_Click(object sender, RoutedEventArgs e)
        {
            await CortanaHelper.LaunchCortanaToAboutMeAsync();
        }

        private void TimeZoneComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ComboBox comboBox = sender as ComboBox;

            if (comboBox == null || comboBox.SelectedItem == null)
            {
                return;
            }

            if (TimeZoneSettings.CanChangeTimeZone)
            {
                string newTimeZone = comboBox.SelectedItem as string;
                TimeZoneSettings.ChangeTimeZoneByDisplayName(newTimeZone);
            }
        }
    }
}