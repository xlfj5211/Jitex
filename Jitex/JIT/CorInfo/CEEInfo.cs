﻿using System;
using System.Runtime.InteropServices;

namespace Jitex.JIT.CorInfo
{
    internal class CEEInfo
    {
        private readonly IntPtr _corJitInfo;

        private readonly GetMethodModuleDelegate _getMethodModule;

        private readonly GetMethodDefFromMethodDelegate _getMethodDefFromMethod;

        private readonly ResolveTokenDelegate _resolveToken;

        //ResolveToken is hooked by managed jit.
        public IntPtr ResolveTokenIndex { get; }

        [UnmanagedFunctionPointer(default)]
        public delegate uint GetMethodDefFromMethodDelegate(IntPtr thisHandle, IntPtr hMethod);

        [UnmanagedFunctionPointer(default)]
        public delegate IntPtr GetMethodModuleDelegate(IntPtr thisHandle, IntPtr hMethod);

        [UnmanagedFunctionPointer(default)]
        public delegate void ResolveTokenDelegate(IntPtr thisHandle, ref CORINFO_RESOLVED_TOKEN pResolvedToken);

        public CEEInfo(IntPtr corJitInfo)
        {
            _corJitInfo = corJitInfo;

            string clrVersion = Environment.Version.ToString();

            IntPtr getMethodModuleIndex = IntPtr.Zero;
            IntPtr getMethodDefFromMethodIndex = IntPtr.Zero;

            switch (clrVersion)
            {
                case "3.1.1":
                    getMethodModuleIndex = _corJitInfo + IntPtr.Size * 10;
                    ResolveTokenIndex = _corJitInfo + IntPtr.Size * 28;
                    getMethodDefFromMethodIndex = _corJitInfo + IntPtr.Size * 116;
                    break;
            }

            IntPtr getMethodModulePtr = Marshal.ReadIntPtr(getMethodModuleIndex);
            IntPtr resolveTokenPtr = Marshal.ReadIntPtr(ResolveTokenIndex);
            IntPtr getMethodDefFromMethodPtr = Marshal.ReadIntPtr(getMethodDefFromMethodIndex);

            _getMethodModule = Marshal.GetDelegateForFunctionPointer<GetMethodModuleDelegate>(getMethodModulePtr);
            _getMethodDefFromMethod = Marshal.GetDelegateForFunctionPointer<GetMethodDefFromMethodDelegate>(getMethodDefFromMethodPtr);
            _resolveToken = Marshal.GetDelegateForFunctionPointer<ResolveTokenDelegate>(resolveTokenPtr);
        }

        public uint GetMethodDefFromMethod(IntPtr hMethod)
        {
            return _getMethodDefFromMethod(_corJitInfo, hMethod);
        }

        public IntPtr GetMethodModule(IntPtr hMethod)
        {
            return _getMethodModule(_corJitInfo, hMethod);
        }

        public void ResolveToken(IntPtr thisHandle, ref CORINFO_RESOLVED_TOKEN pResolvedToken)
        {
            _resolveToken(thisHandle, ref pResolvedToken);
        }
    }
}