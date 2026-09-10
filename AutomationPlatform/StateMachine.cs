//---------------------------------------------------------------------------------------------
// Copyright (c) 2022, Siemens Industry, Inc.
// All rights reserved.
//
// Filename:   StateMachine.cs
//
// Purpose:    This class is what drives the "State Machine" appearance of the program.
//
//---------------------------------------------------------------------------------------------

using Microsoft.VisualBasic;
using Siemens.Automation.AutomationTool.API;
using Siemens.Automation.OMSPlus.IDs;
using System.Runtime.CompilerServices;

namespace AutomationPlatform
{
	internal class StateMachine
	{
		private readonly ResourceStrings resoureString = new();
		// Default to English before a language is selected
		private Language language = Language.English;
		private ICPU? CurrentCPU;
		Network m_network = new();
		Result result = new Result();
		private int progressPercentage = -1;
		private readonly ResourceStrings resourceString = new();
		private uint badIP = 0xffffffff;
		List<String>? NetworkInterfaces;

		// This example program is written as a simple state machine
		// It will enter an infinite loop transitioning through the different states as defined
		// in this enumeration until the user decides to exit the program
		// The states are listed in the order in which they are presented on the screen
		enum ApplicationStates
		{
			Introduction,
			LanguageSelection,
			NICSelection,
			IPAddressEntry,
			CertificateTrustSelection,
			CPUPasswordEntry,
			CommandSelection,
			ExitApplication
		}
		// Set the starting state to the Introduction state
		ApplicationStates theState = ApplicationStates.Introduction;

		// This method transitions between the different states, prompting and processing user input
		public void Start()
		{
			while (true)
			{
				switch (theState)
				{
					case ApplicationStates.Introduction:
						// SIMATIC Automation Tool SDK Command Line Example Program
						// Press 'Ctrl+C' at any time to quit
						Console.WriteLine("SIMATIC Automation Tool SDK Command Line Example Program");
						Console.WriteLine("Press 'Ctrl+C' at any time to quit");

						// Move to language selection state
						theState = ApplicationStates.LanguageSelection;
						break;

					case ApplicationStates.LanguageSelection:
						// If the function returns TRUE then the language variable will be set
						// If the function returns FALSE then the user made a data entry error
						if (SetLanguage())
						{
							theState = ApplicationStates.NICSelection;
							continue;
						}
						Console.WriteLine(resourceString.GetString("commandError", language));
						break;

					case ApplicationStates.NICSelection:
						// Determine if there are Network Interface Cards (NIC) present
						// If this function returns FALSE there are no NICs to choose from, thus exit the program
						if (!DetermineNICOptions())
						{
							Console.WriteLine(resourceString.GetString("noNICOptions", language));
							theState = ApplicationStates.ExitApplication;
							continue;
						}
						// Prompt the user to select a NIC
						// If the function returns TRUE then the user selected a NIC
						// If the function returns FALSE then the user made a data entry error
						if (SetNIC())
						{
							theState = ApplicationStates.IPAddressEntry;
							continue;
						}
						Console.WriteLine(resourceString.GetString("commandError", language));
						break;

					case ApplicationStates.IPAddressEntry:
						// Prompt the user to enter an IP address of a CPU to make a connection
						// If the function returns TRUE then a valid CPU IP address was entered
						// If the function returns FALSE then the user made a data entry error and the function shall output the error to the screen
						if (GetIP())
						{
							theState = ApplicationStates.CertificateTrustSelection;
						}
						break;

					case ApplicationStates.CertificateTrustSelection:
						// Allow the user to choose if they want to trust the certificate
						if (TrustCertificate())
						{
							theState = ApplicationStates.CPUPasswordEntry;
						}
						break;

					case ApplicationStates.CPUPasswordEntry:
						// Get the password from the user
						if (GetPassword())
						{
							theState = ApplicationStates.CommandSelection;
						}
						break;

					case ApplicationStates.CommandSelection:
						// Allow the user to choose a command
						Commands();
						break;

					case ApplicationStates.ExitApplication:
						// Returning will exit the infinite while loop
						return;

					default:
						// Defensive coding all states should be handled without default
						Console.WriteLine(resourceString.GetString("internalError", language));
						theState = ApplicationStates.ExitApplication;
						break;
				}
			}
		}

		// This method allows the user to select the language that the program will use when outputting to the screen
		private bool SetLanguage()
		{
			// Select from the following language options:
			// 1: Deutsch
			// 2: English
			// 3: Español
			// 4: Français
			// 5: Italiano
			// 6: Chinese
			// Language Selection:

			Console.WriteLine();
			Console.WriteLine("Select from the following language options: ");
			Console.WriteLine("	1: Deutsch");
			Console.WriteLine("	2: English");
			Console.WriteLine("	3: Español");
			Console.WriteLine("	4: Français");
			Console.WriteLine("	5: Italiano");
			Console.WriteLine("	6: Chinese");
			Console.WriteLine();
			Console.Write("Language Selection: ");
			string languageChoice = ReadLine();

			languageChoice = TrimSpaces(languageChoice);

			switch (languageChoice)
			{
				case "1":
					// Language is German
					language = Language.German;
					return true;
				case "2":
					// Language is English
					language = Language.English;
					return true;
				case "3":
					// Language is Spanish
					language = Language.Spanish;
					return true;
				case "4":
					// Language is French
					language = Language.French;
					return true;
				case "5":
					// Language is Italian
					language = Language.Italian;
					return true;
				case "6":
					// Language is Chinese
					language = Language.Chinese;
					return true;
				default:
					return false;
			}
		}

		// This method determines if there are NIC options available 
		private bool DetermineNICOptions()
		{
			// Get a list of all NICs
			result = m_network.QueryNetworkInterfaceCards(out NetworkInterfaces);
			if (result.Succeeded)
			{
				return true;
			}
			PrintMessages(result);
			return false;
		}

		// This method is designed to get all the network interface cards and allow a user to select on. Once they do it calls the API function to set the NIC.
		public bool SetNIC()
		{
			Console.WriteLine();
			Console.WriteLine(resourceString.GetString("selectNICQuestion", language));
			int i = 0;
			foreach (var item in NetworkInterfaces)
			{
				i++;
				// Print the NICs 
				Console.WriteLine("	" + i + ": " + item);
			}
			Console.WriteLine();
			Console.Write(resourceString.GetString("networkInterfacePrompt", language) + " ");
			var NICNumString = ReadLine();
			NICNumString = TrimSpaces(NICNumString);
			int.TryParse(NICNumString, out int NICNum);
			if (NICNum <= i && NICNum > 0)
			{
				string NIC = NetworkInterfaces[NICNum - 1];

				// Call API to set the NIC
				result = m_network.SetCurrentNetworkInterface(NIC);
				PrintMessages(result);
				if (result.Succeeded)
				{
					return true;
				}
			}
			return false;
		}

		// This method gets the desired CPU IP address from the user, only CPUs are supported
		public bool GetIP()
		{
			Console.WriteLine();
			Console.Write(resourceString.GetString("targetIPAddressPrompt", language) + " ");
			var rawIPAddress = ReadLine();
			rawIPAddress = TrimSpaces(rawIPAddress);
			var IPAddress = ParseIP(rawIPAddress);
			
			if (IPAddress == badIP)
			{
				return false;
			}
			var Device = Network.GetEmptyCollection();

			// Add device to device table
			IProfinetDevice InsertedDevice;
			IScanErrorCollection InsertErrorCollection = Device.InsertDeviceByIP(IPAddress,0, 0xffffff00,null, out InsertedDevice);
			if (InsertErrorCollection.Failed)
			{
				// Output error messages to the screen if the insert fails
				PrintMessages(InsertErrorCollection);
				return false;
			}

			// Only CPUs are supported by this application
			CurrentCPU = InsertedDevice as ICPU;
			if (CurrentCPU == null)
			{
				Console.WriteLine(resourceString.GetString("notSupportedError", language));
				return false;
			}
            
            // Output the success message to the screen
            PrintMessages(InsertErrorCollection);
			return true;
		}

		// If needed, this method allows the user to trust the CPU certificate
		private bool TrustCertificate()
		{
			// Secure TLS communications might be enabled. If TLS is not enabled then trusting the certificate is not necessary.
			// If TLS is enabled the user must trust the certificate if it is not automatically trusted because it is signed.
			if (CurrentCPU.TLSTrustRequired)
			{
				if (CurrentCPU.TrustCertificateStore != TrustCertificateType.Always)
				{
					// TLS secure connection detected. Choose a certificate trust option:
					// 1: Always
					// 2: Never

					// Certificate Option:
					Console.WriteLine();
					Console.WriteLine(resourceString.GetString("TLSQuestion", language));
					Console.WriteLine(resourceString.GetString("TLSAlwaysOption", language));
					Console.WriteLine(resourceString.GetString("TLSNeverOption", language));
					Console.WriteLine();
					Console.Write(resourceString.GetString("certificateOptionPrompt", language));
					var certificateTrustOption = ReadLine();
					certificateTrustOption = TrimSpaces(certificateTrustOption);
					switch (certificateTrustOption)
					{
						// Accept the certificate
						case "1":
							result = CurrentCPU.SetTrustCertificateStore(TrustCertificateType.Always);
							PrintMessages(result);
							break;
						// Reject the certificate and start over at the IP address entry
						case "2":
							result = CurrentCPU.SetTrustCertificateStore(TrustCertificateType.Never);
							PrintMessages(result);
							Console.WriteLine(resourceString.GetString("communicationsDisabledWarning", language));
							theState = ApplicationStates.IPAddressEntry;
							return false;
						// Error handling if the user puts in a bad input
						default:
							Console.WriteLine(resourceString.GetString("commandError", language));
							return false;
					}
				}
			}
			return true;
		}

		// This method prompts the user for the CPU password and then validates it
		private bool GetPassword()
		{
			// The CPU might be protected. If the CPU is protected then the read-write password is needed for the functionality of this program.
			if (CurrentCPU.Protected)
			{
				Console.WriteLine();

				bool bPromptUserName = false;
				bool bPromptPassword = false;
				ConnectionType loginType = ConnectionType.AccessLevel;
				String user = null;
				String password = null;

				if (CurrentCPU.AdvancedConnectionSupported)
				{
					Console.WriteLine();
					Console.WriteLine(resourceString.GetString("connectionAdvanced", language));
					Console.WriteLine("\t" + resourceString.GetString("connectionAdvanced1", language));
					Console.WriteLine("\t" + resourceString.GetString("connectionAdvanced2", language));
					Console.WriteLine("\t" + resourceString.GetString("connectionAdvanced3", language));
					Console.WriteLine();
					Console.Write("Advanced authorization Option: ");
					var authorizationOption = ReadLine();
					authorizationOption = TrimSpaces(authorizationOption);
					switch (authorizationOption)
					{
						case "1":
							loginType = ConnectionType.AccessLevel;
							bPromptUserName = false;
							bPromptPassword = true;
							break;

						case "2":
							loginType = ConnectionType.LocalProjectUser;
							bPromptUserName = true;
							bPromptPassword = true;
							break;

						case "3":
							loginType = ConnectionType.AnonymousUser;
							bPromptUserName = false;
							bPromptPassword = false;
							break;

						// Error handling if the user puts in a bad input
						default:
							Console.WriteLine(resourceString.GetString("commandError", language));
							return false;
					}
				}
				else
				{
					bPromptUserName = false;
					bPromptPassword = true;
				}

				// Prompt for user name?
				if (bPromptUserName)
				{
					Console.WriteLine();
					Console.Write(resourceString.GetString("connectionAdvancedUserName", language));
					user = ReadLine();
					user = TrimSpaces(user);

					// Check for a NULL or empty project user string
					// If this is the case then output an error and prompt again
					if (user == String.Empty)
					{
						Console.WriteLine(resourceString.GetString("connectionAdvancedUserNameError", language));
						return false;
					}
				}

				// Prompt for user password?
				if (bPromptPassword)
				{
					Console.WriteLine();
					Console.Write(resourceString.GetString("enterPasswordQuestion", language) + " ");
					password = ReadLine();
					password = TrimSpaces(password);

					// Check for a NULL or empty password string
					// If this is the case then output an error and prompt again
					if (password == String.Empty)
					{
						Console.WriteLine(resourceString.GetString("emptyPasswordError", language));
						return false;
					}
				}

				// Call API to set the CPU password
				result = CurrentCPU.SetPassword(new AuthorizationData(loginType, user, password));
				if (result.Failed)
				{
					PrintMessages(result);
					return false;
				}

				// Check that the password entered has sufficient access level for this program
				bool bSufficientAccess = CurrentCPU.PasswordProtectionLevel == ProtectionLevel.Failsafe || CurrentCPU.PasswordProtectionLevel == ProtectionLevel.Full;
				if (CurrentCPU.PasswordValid && !bSufficientAccess)
				{
					Console.WriteLine(resourceString.GetString("insufficientAccessError", language));
					return false;
				}

				// Output the success message to the screen
				PrintMessages(result);
			}

			return true;
		}

		// This method presents the user with each of the command choices and allows them to select one
		private void Commands()
		{
			// Commands
			Console.WriteLine();
			Console.WriteLine(resoureString.GetString("commandQuestion", language));
			Console.WriteLine(resoureString.GetString("identify", language));
			Console.WriteLine(resoureString.GetString("basicDeviceInfo", language));
			Console.WriteLine(resoureString.GetString("moduleInfo", language));
			Console.WriteLine(resoureString.GetString("changeOperatingState", language));
			Console.WriteLine(resoureString.GetString("setIP", language));
			Console.WriteLine(resoureString.GetString("setPROFINET", language));
			Console.WriteLine(resoureString.GetString("firmwareUpdate", language));
			Console.WriteLine(resoureString.GetString("pickNewDevice", language));
			Console.WriteLine(resoureString.GetString("exit", language));
			Console.WriteLine();
			Console.Write(resoureString.GetString("promptForCommand", language));
			var command = ReadLine();
			command = TrimSpaces(command);

			switch (command)
			{
				// Identify
				case "1":
					Identify(CurrentCPU);
					break;
				// Read Basic Device Information 
				case "2":
					PrintBasicDeviceInformation(CurrentCPU);
					break;
				// Read Module Information
				case "3":
					PrintModuleInformation(CurrentCPU);
					break;
				// Change Operating state
				case "4":
					ChangeState();
					break;
				// Set IP Address
				case "5":
					SetIP(CurrentCPU);
					break;
				// Set PROFINET Name
				case "6":
					SetProfinetName(CurrentCPU);
					break;
				// Firmware Update
				case "7":
					FirmwareUpdate(CurrentCPU);
					break;
				// Pick New Device
				case "8":
					theState = ApplicationStates.IPAddressEntry;
					break;
				// Exit
				case "9":
					theState = ApplicationStates.ExitApplication;
					break;
				// Error
				default:
					Console.WriteLine(resoureString.GetString("commandError", language));
					break;
			}
			CheckForChanges();
		}

		// This method is a concise way to check for potential errors between commands
		private void CheckForChanges()
		{
			// If the identity of the CPU has changed 
			if (CurrentCPU.IdentityCrisis)
			{
				Console.WriteLine();
				Console.WriteLine(resoureString.GetString("identityCrisisError", language));
				theState = ApplicationStates.IPAddressEntry;
			}
			// If the certificate has changed
			if (CurrentCPU.TLSTrustRequired && CurrentCPU.TrustCertificateStore == TrustCertificateType.SelectionNeeded)
			{
				Console.WriteLine();
				Console.WriteLine(resoureString.GetString("certificateChanged", language));
				theState = ApplicationStates.CertificateTrustSelection;
			}
			// If the password has changed
			if (CurrentCPU.Protected && !CurrentCPU.PasswordValid)
			{
				Console.WriteLine();
				Console.WriteLine(resoureString.GetString("passwordChange", language));
				theState = ApplicationStates.CPUPasswordEntry;
			}
		}

		// This method calls the API Identify command
		public void Identify(ICPU? CurrentCPU)
		{
			//Call API to identify the CPU
			result = CurrentCPU.Identify();
			PrintMessages(result);
			//CurrentCPU.Selected = true;


			//List<DiagnosticsItem> aLogs = new List<DiagnosticsItem>();
			//         var  retVal = CurrentCPU.GetDiagnosticsBuffer(out aLogs, Language.English);
			//         if (retVal.Succeeded)
			//         {
			//             for (int idxLog = 0; idxLog < aLogs.Count; idxLog++)
			//             {
			//                 //---------------------------
			//                 //获取信息时间戳
			//                 //---------------------------
			//                 string descrTimes = aLogs[idxLog].TimeStamp.ToString();
			//		Console.WriteLine(descrTimes);
			//                 //----------------------------------
			//                 //或许信息基本说明
			//                 //----------------------------------
			//                 string descrBasic = aLogs[idxLog].Description1;
			//                 Console.WriteLine(descrBasic);
			//                 //--------------------------------------
			//                 //获取信息详细说明
			//                 //--------------------------------------
			//                 string descrDetail = aLogs[idxLog].Description2;
			//                 Console.WriteLine(descrDetail);
			//                 //-----------------------------------------
			//                 //获取信息状态（传入/传出）
			//                 //-----------------------------------------
			//                 string descrState = aLogs[idxLog].State.ToString();
			//                 Console.WriteLine(descrState);
			//             }
			//         }

			//         var res = CurrentCPU.UploadServiceData(@"C:\Diagnostics");
			//         PrintMessages(res);

			//         CurrentCPU.Selected = false;
		}

		// This method reads and prints the basic device information of the CPU
		public void PrintBasicDeviceInformation(ICPU? CurrentCPU)
		{
			// A refresh status is needed here because this command is not coming directly from the API.
			result = CurrentCPU.RefreshStatus(false);
			if (result.Failed)
			{
				PrintMessages(result);
				return;
			}
			Console.WriteLine();

			// Type of Device
			Console.WriteLine(resourceString.GetString("DeviceTypeLabel", language) + " " + CurrentCPU.Description);
			// Article Number
			Console.WriteLine(resourceString.GetString("articleNumberLabel", language) + " " + CurrentCPU.ArticleNumber);
			// Serial Number
			Console.WriteLine(resourceString.GetString("serialNumberLabel", language) + " " + CurrentCPU.SerialNumber);
			// Hardware Number
			Console.WriteLine(resourceString.GetString("hardwareNumberLabel", language) + " " + CurrentCPU.HardwareNumber);
			// Firmware Version
			Console.WriteLine(resourceString.GetString("firmwareVersionLabel", language) + " " + CurrentCPU.FirmwareVersion);
			// MAC Address
			Console.WriteLine(resourceString.GetString("MACLabel", language) + " " + CurrentCPU.MACString);
			// IP Address
			Console.WriteLine(resourceString.GetString("ipPrompt", language) + " " + CurrentCPU.IPString);
			// PROFINET Name
			Console.WriteLine(resourceString.GetString("PROFINETLable", language) + " " + CurrentCPU.ProfinetName);
			// Operating State
			Console.WriteLine(resourceString.GetString("operatingStateLabel", language) + " " + CurrentCPU.OperatingMode.ToString().ToUpper());
			// Totally Integrated Automation Portal Version
			Console.WriteLine(resourceString.GetString("TIAPVersionLabel", language) + " " + CurrentCPU.TIAPVersion);
		}

		// This method reads and prints the module information for all local modules connected to the CPU
		public void PrintModuleInformation(ICPU? CurrentCPU)
		{
			// A refresh status is needed here because this command is not coming directly from the API.
			result = CurrentCPU.RefreshStatus(false);
			PrintMessages(result);
			if (result.Failed)
			{
				return;
			}

			// If there are no Modules
			if (CurrentCPU.Modules.Count == 0)
			{
				Console.WriteLine(resourceString.GetString("noModulesFoundError", language));
				return;
			}

			// Loop over all modules and print each one
			foreach (var module in CurrentCPU.Modules)
			{
				Console.WriteLine();
				// Name
				Console.WriteLine(resourceString.GetString("nameLabel", language) + " " + module.Name);
				// Type of Device
				Console.WriteLine(resourceString.GetString("DeviceTypeLabel", language) + " " + module.Description);
				// Slot number
				Console.WriteLine(resourceString.GetString("slotLabel", language) + " " + module.SlotName);
				// Configuration Status
				Console.WriteLine(resourceString.GetString("configurationLabel", language) + " " + module.StatusConfiguration);
				// Article Number
				Console.WriteLine(resourceString.GetString("articleNumberLabel", language) + " " + module.ArticleNumber);
				// Serial Number
				Console.WriteLine(resourceString.GetString("serialNumberLabel", language) + " " + module.SerialNumber);
				// Hardware Number
				Console.WriteLine(resourceString.GetString("hardwareNumberLabel", language) + " " + module.HardwareNumber);
				// Firmware Version
				Console.WriteLine(resourceString.GetString("firmwareVersionLabel", language) + " " + module.FirmwareVersion);
			}
		}

		// This method allows the user to change the operating state of the CPU.
		public void ChangeState()
		{
			Console.WriteLine();
			Console.WriteLine(resourceString.GetString("operatingStateQuestion", language));
			Console.WriteLine(resourceString.GetString("runChoice", language));
			Console.WriteLine(resourceString.GetString("stopChoice", language));
			Console.WriteLine();
			Console.Write(resourceString.GetString("statePrompt", language));
			var state = ReadLine();
			state = TrimSpaces(state);
			CurrentCPU.Selected = true;
			switch (state)
			{
				//Run
				case "1":
					result = CurrentCPU.SetOperatingState(OperatingStateREQ.Run);
					PrintMessages(result);
					break;
				//Stop
				case "2":
					result = CurrentCPU.SetOperatingState(OperatingStateREQ.Stop);
					PrintMessages(result);
					break;
				//Error
				default:
					Console.WriteLine(resourceString.GetString("commandError", language) + " ");
					ChangeState();
					break;
			}
		}

		// This method prompts the user for a new IP address then calls the API to set the new IP address
		public void SetIP(ICPU? CurrentCPU)
		{
			Console.WriteLine();
			Console.Write(resourceString.GetString("enterIPQuestion", language) + " ");
			var newIP = ReadLine();
			newIP = TrimSpaces(newIP);
			var newIPParsed = ParseIP(newIP);
			if (newIPParsed == badIP)
			{
				Console.WriteLine(resourceString.GetString("invalidIP", language));
				return;
			}

			Console.Write(resourceString.GetString("enterSubnetQuestion", language) + " ");
			var subnet = ReadLine();
			subnet = TrimSpaces(subnet);
			var parsedSubnet = ParseIP(subnet);
			if (parsedSubnet == badIP)
			{
				Console.WriteLine(resourceString.GetString("invalidSubnet", language));
				return;
			}

			Console.Write(resourceString.GetString("enterGatewayQuestion", language) + " ");
			var gateway = ReadLine();
			gateway = TrimSpaces(gateway);
			var parsedGateway = ParseIP(gateway);
			if (parsedGateway == badIP)
			{
				Console.WriteLine(resourceString.GetString("invalidGateway", language));
				return;
			}

			result = CurrentCPU.SetIP(newIPParsed, parsedSubnet, parsedGateway);

			PrintMessages(result);

		}

		// This method prompts the user for a new PROFINET name and then calls the API to set the new PROFINET name
		public void SetProfinetName(ICPU? CurrentCPU)
		{
			Console.WriteLine();
			Console.Write(resourceString.GetString("PROFINETQuestion", language) + " ");
			var newProfinetName = ReadLine();
			newProfinetName = TrimSpaces(newProfinetName);
			result = CurrentCPU.SetProfinetName(newProfinetName);
			PrintMessages(result);
		}

		// This method gets a firmware update (UDP) file from the user and then calls the API to update the firmware
		public void FirmwareUpdate(ICPU? CurrentCPU)
		{
			Console.WriteLine();
			Console.Write(resourceString.GetString("UDPFilePathQuestion", language) + " ");
			var udpFile = ReadLine();
			udpFile = TrimSpaces(udpFile);
			// Validate the file
			result = CurrentCPU.SetFirmwareFile(udpFile);
			if (result.Succeeded)
			{
				CurrentCPU.Selected = true;

				// Begin progress bar
				CurrentCPU.ProgressChanged += CurrentCPU_ProgressChanged;
				Console.Write(resourceString.GetString("progressBar", language));
				// Update the firmware
				result = CurrentCPU.FirmwareUpdate(CurrentCPU.ID, true);

				Console.WriteLine();
				PrintMessages(result);
				// Finish progress bar
				CurrentCPU.ProgressChanged -= CurrentCPU_ProgressChanged;
			}
			// File path invalid
			else
			{
				PrintMessages(result);
				FirmwareUpdate(CurrentCPU);
			}
			Console.WriteLine();
		}

		// This method provides a progress bar for the command it is placed around. See Firmware Update for an example.
		private void CurrentCPU_ProgressChanged(object sender, ProgressChangedEventArgs e)
		{
			// If statement ensures we update the bar during each action
			if (e.Action == ProgressAction.Updating || e.Action == ProgressAction.Downloading || e.Action == ProgressAction.Processing || e.Action == ProgressAction.Rebooting)
			{
				if (e.Index != progressPercentage)
				{
					progressPercentage = e.Index;
					// Add item every 5% of progress
					if (progressPercentage % 5 == 0)
					{
						Console.Write(".");
					}
				}
			}
		}

		// This method parses the IP Address that was input by the user into a usable IP for the program.
		public UInt32 ParseIP(string strNetParm)
		{
			string[] splitString = strNetParm.Split(".");
			// Check that the IP has 4 entries
			if (splitString.Length != 4)
			{
				Console.WriteLine(resourceString.GetString("parsingError", language));
				return badIP;
			}
			//Check that each of the entries are non-empty
			foreach (string str in splitString)
			{
				if(str.Length <=0)
				{
					Console.WriteLine(resourceString.GetString("parsingError", language));
					return badIP;
				}
			}
			try
			{
				System.Net.IPAddress ip = System.Net.IPAddress.Parse(strNetParm);
				byte[] bytes = ip.GetAddressBytes();
				Array.Reverse(bytes);
				return BitConverter.ToUInt32(bytes, 0);
			}
			catch
			{
				Console.WriteLine(resourceString.GetString("parsingError", language));
				return badIP;
			}
		}

		// This method gets all success, error, and warning messages from a Result object and prints them.
		private void PrintMessages(Result result)
		{
			// Do we have warnings?
			String[] aWarnings = result.GetWarningDescription(language);
			for (int i = aWarnings.Length - 1; i >= 0; i--)
				Console.WriteLine("WARNING: " + aWarnings[i]);

			// If an error occurred
			if (result.Succeeded)
				Console.WriteLine("SUCCESS: " + result.GetErrorDescription(language));
			else
				Console.WriteLine("ERROR: " + result.GetErrorDescription(language));
		}

		// This method gets all success, error, and warning messages from an IScanErrorCollection object and prints them.
		private void PrintMessages(IScanErrorCollection errorCollection)
		{
			for (int i = errorCollection.Count - 1; i >= 0; i--)
			{
				IScanErrorEvent scanErrorEvent = errorCollection[i];

				Result result = new Result(scanErrorEvent.Code);
				switch (scanErrorEvent.Type)
				{
					case ScanErrorType.Success:
						Console.WriteLine("SUCCESS: " + result.GetErrorDescription(language));
						break;
					case ScanErrorType.Warning:
						Console.WriteLine("WARNING: " + result.GetErrorDescription(language));
						break;
					case ScanErrorType.Information:
						Console.WriteLine("INFORMATION: " + result.GetErrorDescription(language));
						break;
					case ScanErrorType.Error:
						Console.WriteLine("ERROR: " + result.GetErrorDescription(language));
						break;
				}
			}
		}

		//This method reads user input and checks to see if it is null
		private string ReadLine()
		{
			string aString = Console.ReadLine();
			if (aString == null)
				System.Environment.Exit(0);

			return aString;
		}

		// This method is a tool for trimming leading and trailing spaced from user input.
		private string TrimSpaces(string trimString)
		{
			char[] charsToTrim = { ' ' };
			string resultString = trimString.Trim(charsToTrim);
			return resultString;
		}
	}
}