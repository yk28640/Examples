//---------------------------------------------------------------------------------------------
// Copyright (c) 2022, Siemens Industry, Inc.
// All rights reserved.
//
// Filename:      ResourceStrings.cs
//
// Purpose:       This class controls the language translation of the program.
//
//---------------------------------------------------------------------------------------------

using System.Resources;
using System.Reflection;
using System.Globalization;
using Siemens.Automation.AutomationTool.API;

namespace SATExample
{
	internal class ResourceStrings
	{
		ResourceManager rm = new("SATExample.Resources.Language", Assembly.GetExecutingAssembly());

		public ResourceStrings()
		{

		}

		//This method uses the key given to translate the prompts into the desired language.
		public string? GetString(String strKey, Language language)
		{
			String strLanguage;
			switch (language)
			{
				case Language.German:
					strLanguage = "de-DE";
					break;
				case Language.French:
					strLanguage = "fr-FR";
					break;
				case Language.Spanish:
					strLanguage = "es-ES";
					break;
				case Language.English:
					strLanguage = "en-US";
					break;
				case Language.Italian:
					strLanguage = "it-IT";
					break;
				case Language.Chinese:
					strLanguage = "zh-Hans";
					break;
				default:
					strLanguage = "en-US";
					break;
			}
			var str = rm.GetString(strKey, CultureInfo.CreateSpecificCulture(strLanguage));
			return str;
		}
	}
}