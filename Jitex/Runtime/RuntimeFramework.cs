﻿using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Jitex.JIT.CorInfo;

namespace Jitex.Runtime
{
    internal abstract class RuntimeFramework
    {
        /// <summary>
        /// Compile method handler.
        /// </summary>
        /// <param name="thisPtr">this parameter</param>
        /// <param name="comp">(IN) - Pointer to ICorJitInfo.</param>
        /// <param name="info">(IN) - Pointer to CORINFO_METHOD_INFO.</param>
        /// <param name="flags">(IN) - Pointer to CorJitFlag.</param>
        /// <param name="nativeEntry">(OUT) - Pointer to NativeEntry.</param>
        /// <param name="nativeSizeOfCode">(OUT) - Size of NativeEntry.</param>
        /// <returns></returns>
        [UnmanagedFunctionPointer(CallingConvention.StdCall, SetLastError = true)]
        public delegate CorJitResult CompileMethodDelegate(IntPtr thisPtr, IntPtr comp, IntPtr info, uint flags, out IntPtr nativeEntry, out int nativeSizeOfCode);

        /// <summary>
        /// Returns if runtime is .NET Core or .NET Framework
        /// </summary>
        public bool IsCore { get; }

        /// <summary>
        /// Version of framework running.
        /// </summary>
        public Version FrameworkVersion { get; private set; }

        /// <summary>
        /// Address of JIT.
        /// </summary>
        public IntPtr Jit { get; }

        /// <summary>
        /// Address of table JIT.
        /// </summary>
        public IntPtr ICorJitCompileVTable { get; }

        /// <summary>
        /// Address of ICorJitInfo.
        /// </summary>
        public IntPtr CEEInfoVTable { get; set; }

        /// <summary>
        /// Compile method delegate.
        /// </summary>
        public CompileMethodDelegate CompileMethod;

        /// <summary>
        /// Runtime running.
        /// </summary>
        private static RuntimeFramework Framework { get; set; }

        /// <summary>
        /// Load info from JIT.
        /// </summary>
        protected RuntimeFramework(bool isCore)
        {
            IsCore = isCore;
            Jit = GetJitAddress();
            ICorJitCompileVTable = Marshal.ReadIntPtr(Jit);
            IntPtr compileMethodPtr = Marshal.ReadIntPtr(ICorJitCompileVTable);
            CompileMethod = Marshal.GetDelegateForFunctionPointer<CompileMethodDelegate>(compileMethodPtr);
            IdentifyFrameworkVersion();
        }

        /// <summary>
        /// Get running framework.
        /// </summary>
        /// <returns></returns>
        public static RuntimeFramework GetFramework()
        {
            if (Framework != null)
                return Framework;

            string frameworkRunning = RuntimeInformation.FrameworkDescription;

            if (frameworkRunning.StartsWith(".NET Core"))
                Framework = new NETCore();
            else if (frameworkRunning.StartsWith(".NET Framework"))
                Framework = new NETFramework();
            else
                throw new NotSupportedException($"Framework {frameworkRunning} is not supported!");

            return Framework;
        }

        /// <summary>
        /// Get address of method getJit.
        /// </summary>
        /// <returns></returns>
        protected abstract IntPtr GetJitAddress();

        /// <summary>
        /// Read CORJitInfo.
        /// </summary>
        /// <param name="iCorJitInfo">Address to ICorJitInfo.</param>
        public void ReadICorJitInfoVTable(IntPtr iCorJitInfo)
        {
            CEEInfoVTable = Marshal.ReadIntPtr(iCorJitInfo);
        }

        private void IdentifyFrameworkVersion()
        {
            Assembly assembly = typeof(System.Runtime.GCSettings).GetTypeInfo().Assembly;
            string[] assemblyPath = assembly.CodeBase.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);

            string frameworkName = IsCore ? "Microsoft.NETCore.App" : "Framework64";

            int frameworkIndex = Array.IndexOf(assemblyPath, frameworkName);

            if (frameworkIndex > 0 && frameworkIndex < assemblyPath.Length - 2)
            {
                string version = assemblyPath[frameworkIndex + 1];

                if (!IsCore)
                    version = version[1..];
                int[] versionsNumbers = version.Split('.').Select(int.Parse).ToArray();
                FrameworkVersion = new Version(versionsNumbers[0], versionsNumbers[1], versionsNumbers[2]);
            }
            else
            {
                throw new NotSupportedException("Invalid Framework");
            }
        }
    }
}
