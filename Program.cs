//---------------------------------------------------------------------------------------------
// Copyright (c) 2022, Siemens Industry, Inc.
// All rights reserved.
//
// Filename:      Program.cs
//
// Purpose:       This is the base class for the program and controls the start up screen of the program.
//
//---------------------------------------------------------------------------------------------

//using System.Runtime.InteropServices;

//namespace SATExample
//{
//	internal class Program
//	{
		
//		static void Main()
//		{
//			//  Kick off state machine behavior
//			StateMachine machine = new StateMachine();
//			machine.Start();
//		}
//	}
//}

using Siemens.Automation.AutomationTool.API; // SIMATIC Automation Tool V3.1 SP3
using Siemens.Automation.OMSPlus.IDs;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;

namespace SATExample
{
    class Program
    {
        static void Main(string[] args)
        {

            StateMachine sm = new StateMachine();
            sm.Start();

            //uint targetIPAddress = 0xC0A80001; // 192.168.0.1
            //string strDiagFolder = @"c:\Diagnostics";
            //IProfinetDevice dev = devices.FindDeviceByIP(targetIPAddress);
            //if (dev != null)
            //{
            //    ICPU myCPU = dev as ICPU;
            //    if (myCPU != null)
            //    {
            //        myCPU.SetPassword(new AuthorizationData(ConnectionType.AccessLevel,
            //        "", "Password"));
            //        myCPU.Selected = true;
            //        if (myCPU.OperatingMode == OperatingState.Defective)
            //        {
            //            // 获取采用默认本地时间戳格式的服务数据
            //            Result retVal = myCPU.UploadServiceData(strDiagFolder);

            //            // 获取采用 UTC 时间戳格式的服务数据
            //            retVal = myCPU.UploadServiceData(strDiagFolder,
            //            TimeFormat.UTC);
            //        }
            //        myCPU.Selected = false;
            //    }
            //}

        }
    }
}