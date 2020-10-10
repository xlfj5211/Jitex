﻿using Jitex.Hook;
using Jitex.JIT.CorInfo;
using Jitex.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;
using Jitex.Exceptions;
using Jitex.JIT.Context;
using static Jitex.JIT.CorInfo.CEEInfo;
using static Jitex.JIT.CorInfo.CorJitCompiler;
using static Jitex.JIT.JitexHandler;

using MethodBody = Jitex.Builder.Method.MethodBody;

namespace Jitex.JIT
{
    /// <summary>
    /// Handlers to expose hooks.
    /// </summary>
    public static class JitexHandler
    {
        /// <summary>
        /// Method resolver handler.
        /// </summary>
        /// <param name="context">Context of method.</param>
        public delegate void MethodResolverHandler(MethodContext context);

        /// <summary>
        /// Token resolver handler.
        /// </summary>
        /// <param name="context">Context of token.</param>
        public delegate void TokenResolverHandler(TokenContext context);
    }

    /// <summary>
    /// Hook instance from JIT.
    /// </summary>
    internal class ManagedJit : IDisposable
    {
        [DllImport("clrjit.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true, EntryPoint = "getJit", BestFitMapping = true)]
        private static extern IntPtr GetJit();

        private readonly HookManager _hookManager = new HookManager();

        /// <summary>
        /// Custom compíle method.
        /// </summary>
        private CompileMethodDelegate _compileMethod;

        /// <summary>
        /// Custom resolve token.
        /// </summary>
        private ResolveTokenDelegate _resolveToken;

        /// <summary>
        /// Custom construct string literal.
        /// </summary>
        private ConstructStringLiteralDelegate _constructStringLiteral;

        private bool _isDisposed;

        [ThreadStatic] private static CompileTls _compileTls;

        [ThreadStatic] private static TokenTls _tokenTls;

        private static readonly object InstanceLock = new object();
        private static readonly object JitLock = new object();

        private static ManagedJit _instance;

        private static readonly IntPtr JitVTable;

        private static readonly CorJitCompiler Compiler;

        private static IntPtr _corJitInfoPtr = IntPtr.Zero;

        private static CEEInfo _ceeInfo;

        private MethodResolverHandler _methodResolvers;

        private TokenResolverHandler _tokenResolvers;

        public static bool IsLoaded => _instance != null;

        static ManagedJit()
        {
            IntPtr jit = GetJit();

            JitVTable = Marshal.ReadIntPtr(jit);
            Compiler = Marshal.PtrToStructure<CorJitCompiler>(JitVTable);
        }

        internal void AddMethodResolver(MethodResolverHandler methodResolver) => _methodResolvers += methodResolver;

        internal void AddTokenResolver(TokenResolverHandler tokenResolver) => _tokenResolvers += tokenResolver;

        internal void RemoveMethodResolver(MethodResolverHandler methodResolver) => _methodResolvers -= methodResolver;

        internal void RemoveTokenResolver(TokenResolverHandler tokenResolver) => _tokenResolvers -= tokenResolver;

        internal bool HasMethodResolver(MethodResolverHandler methodResolver) => _methodResolvers != null && _methodResolvers.GetInvocationList().Any(del => del.Method == methodResolver.Method);

        internal bool HasTokenResolver(TokenResolverHandler tokenResolver) => _tokenResolvers != null && _tokenResolvers.GetInvocationList().Any(del => del.Method == tokenResolver.Method);

        /// <summary>
        ///     Prepare custom JIT.
        /// </summary>
        private ManagedJit()
        {
            _compileMethod = CompileMethod;
            _resolveToken = ResolveToken;
            _constructStringLiteral = ConstructStringLiteral;

            CORINFO_METHOD_INFO emptyInfo = default;
            CORINFO_RESOLVED_TOKEN corinfoResolvedToken = default;

            RuntimeHelperExtension.PrepareDelegate(_compileMethod, IntPtr.Zero, IntPtr.Zero, emptyInfo, (uint)0, IntPtr.Zero, 0);
            RuntimeHelperExtension.PrepareDelegate(_resolveToken, IntPtr.Zero, corinfoResolvedToken);
            RuntimeHelperExtension.PrepareDelegate(_constructStringLiteral, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

            _hookManager.InjectHook(JitVTable, _compileMethod);
        }

        /// <summary>
        /// Get singleton instance from ManagedJit.
        /// </summary>
        /// <returns></returns>
        internal static ManagedJit GetInstance()
        {
            lock (InstanceLock)
            {
                _instance ??= new ManagedJit();
                return _instance;
            }
        }

        /// <summary>
        ///     Wrap delegate to compileMethod from ICorJitCompiler.
        /// </summary>
        /// <param name="thisPtr">this parameter.</param>
        /// <param name="comp">(IN) - Pointer to ICorJitInfo.</param>
        /// <param name="info">(IN) - Pointer to CORINFO_METHOD_INFO.</param>
        /// <param name="flags">(IN) - Pointer to CorJitFlag.</param>
        /// <param name="nativeEntry">(OUT) - Pointer to NativeEntry.</param>
        /// <param name="nativeSizeOfCode">(OUT) - Size of NativeEntry.</param>
        private CorJitResult CompileMethod(IntPtr thisPtr, IntPtr comp, ref CORINFO_METHOD_INFO info, uint flags, out IntPtr nativeEntry, out int nativeSizeOfCode)
        {
            CompileTls compileEntry = _compileTls ??= new CompileTls();
            compileEntry.EnterCount++;

            try
            {
                if (thisPtr == default)
                {
                    nativeEntry = IntPtr.Zero;
                    nativeSizeOfCode = 0;
                    return 0;
                }

                MethodContext methodContext = null;
                IntPtr sigAddress = IntPtr.Zero;
                IntPtr ilAddress = IntPtr.Zero;

                if (compileEntry.EnterCount == 1)
                {
                    IEnumerable<Delegate> resolvers = _methodResolvers?.GetInvocationList();

                    if (resolvers != null && resolvers.Any())
                    {
                        lock (JitLock)
                        {
                            if (_corJitInfoPtr == IntPtr.Zero)
                            {
                                _corJitInfoPtr = Marshal.ReadIntPtr(comp);
                                _ceeInfo = new CEEInfo(_corJitInfoPtr);

                                _hookManager.InjectHook(_ceeInfo.ResolveTokenIndex, _resolveToken);
                                _hookManager.InjectHook(_ceeInfo.ConstructStringLiteralIndex, _constructStringLiteral);
                            }
                        }

                        Module module = AppModules.GetModuleByPointer(info.scope);

                        if (module != null)
                        {
                            uint methodToken = _ceeInfo.GetMethodDefFromMethod(info.ftn);
                            MethodBase methodFound = module.ResolveMethod((int)methodToken);

                            if (methodFound == null)
                                Debugger.Break();

                            _tokenTls = new TokenTls { Root = methodFound };

                            methodContext = new MethodContext(methodFound);

                            foreach (MethodResolverHandler resolver in resolvers)
                            {
                                resolver(methodContext);

                                if (methodContext.IsResolved)
                                    break;
                            }
                        }

                        if (methodContext != null && methodContext.IsResolved)
                        {
                            int ilLength;

                            if (methodContext.Mode == MethodContext.ResolveMode.IL)
                            {
                                MethodBody methodBody = methodContext.MethodBody;

                                ilLength = methodBody.IL.Length;
                                ilAddress = Marshal.AllocHGlobal(methodBody.IL.Length);
                                Marshal.Copy(methodBody.IL, 0, ilAddress, methodBody.IL.Length);

                                if (methodBody.HasLocalVariable)
                                {
                                    byte[] signatureVariables = methodBody.GetSignatureVariables();
                                    sigAddress = Marshal.AllocHGlobal(signatureVariables.Length);
                                    Marshal.Copy(signatureVariables, 0, sigAddress, signatureVariables.Length);
                                    //IntPtr sigAddress = signatureVariables.ToPointer();

                                    info.locals.pSig = sigAddress + 1;
                                    info.locals.args = sigAddress + 3;
                                    info.locals.numArgs = (ushort)methodBody.LocalVariables.Count;
                                }

                                info.maxStack = methodBody.MaxStackSize;
                            }
                            else
                            {
                                //Size of native code generated for AND
                                const int sizeBitwise = 3;

                                //TODO: A better way to calculate size.
                                //Calculate the size of IL to allocate native code
                                //For each bitwise operation (ldc.i4 + And) is generated 3 byte-code
                                //Example: 
                                //IL with 1 bitwise = 21 bytes
                                //IL with 2 bitwise = 24 bytes
                                //IL with 3 bitwise = 27 bytes
                                //...

                                int nextMinLength = methodContext.NativeCode.Length + sizeBitwise + methodContext.NativeCode.Length % sizeBitwise;
                                ilLength = 2 * (int)Math.Ceiling((double)nextMinLength / sizeBitwise);

                                if (ilLength % 2 != 0)
                                    ilLength++;

                                ilAddress = Marshal.AllocHGlobal(ilLength);

                                Span<byte> emptyBody;

                                unsafe
                                {
                                    emptyBody = new Span<byte>(ilAddress.ToPointer(), ilLength);
                                }

                                //populate IL with bitwise operations
                                emptyBody[0] = (byte)OpCodes.Ldc_I4_1.Value;
                                emptyBody[1] = (byte)OpCodes.Ldc_I4_1.Value;
                                emptyBody[2] = (byte)OpCodes.And.Value;
                                emptyBody[^1] = (byte)OpCodes.Ret.Value;

                                for (int i = 3; i < emptyBody.Length - 2; i += 2)
                                {
                                    emptyBody[i] = (byte)OpCodes.Ldc_I4_1.Value;
                                    emptyBody[i + 1] = (byte)OpCodes.And.Value;
                                }

                                if (info.maxStack < 8)
                                {
                                    info.maxStack = 8;
                                }
                            }

                            info.ILCode = ilAddress;
                            info.ILCodeSize = ilLength;
                        }
                    }
                }

                CorJitResult result = Compiler.CompileMethod(thisPtr, comp, ref info, flags, out nativeEntry, out nativeSizeOfCode);

                if (ilAddress != IntPtr.Zero && methodContext!.Mode == MethodContext.ResolveMode.IL)
                    Marshal.FreeHGlobal(ilAddress);

                if (sigAddress != IntPtr.Zero)
                    Marshal.FreeHGlobal(sigAddress);

                if (methodContext?.Mode == MethodContext.ResolveMode.Native)
                {
                    Marshal.Copy(methodContext.NativeCode, 0, nativeEntry, methodContext.NativeCode.Length);
                }

                return result;
            }
            finally
            {
                compileEntry.EnterCount--;
            }
        }

        private void ResolveToken(IntPtr thisHandle, ref CORINFO_RESOLVED_TOKEN pResolvedToken)
        {
            _tokenTls ??= new TokenTls();

            if (thisHandle == IntPtr.Zero)
                return;

            _tokenTls.EnterCount++;

            try
            {
                if (_tokenTls.EnterCount == 1)
                {
                    IEnumerable<Delegate> resolvers = _tokenResolvers?.GetInvocationList();

                    if (resolvers == null || !resolvers.Any())
                    {
                        _ceeInfo.ResolveToken(thisHandle, ref pResolvedToken);
                        return;
                    }

                    //Capture method who trying resolve that token.
                    _tokenTls.Source = _tokenTls.GetSource();

                    TokenContext context = new TokenContext(ref pResolvedToken, _tokenTls.Source);

                    foreach (TokenResolverHandler resolver in resolvers)
                    {
                        resolver(context);

                        if (context.IsResolved)
                        {
                            pResolvedToken = context.ResolvedToken;
                            break;
                        }
                    }
                }

                _ceeInfo.ResolveToken(thisHandle, ref pResolvedToken);
            }
            finally
            {
                _tokenTls.EnterCount--;
            }
        }

        private InfoAccessType ConstructStringLiteral(IntPtr thisHandle, IntPtr hModule, int metadataToken, IntPtr ppValue)
        {
            _tokenTls ??= new TokenTls();

            if (thisHandle == IntPtr.Zero)
                return default;

            _tokenTls.EnterCount++;

            try
            {

                if (_tokenTls.EnterCount == 1)
                {
                    IEnumerable<Delegate> resolvers = _tokenResolvers?.GetInvocationList();

                    if (resolvers == null || !resolvers.Any())
                    {
                        return _ceeInfo.ConstructStringLiteral(thisHandle, hModule, metadataToken, ppValue);
                    }

                    //Capture method who trying resolve that token.
                    _tokenTls.Source = _tokenTls.GetSource();

                    CORINFO_CONSTRUCT_STRING constructString = new CORINFO_CONSTRUCT_STRING(hModule, metadataToken, ppValue);
                    TokenContext context = new TokenContext(ref constructString, _tokenTls.Source);

                    foreach (TokenResolverHandler resolver in resolvers)
                    {
                        resolver(context);

                        if (context.IsResolved)
                        {
                            if (string.IsNullOrEmpty(context.Content))
                                throw new StringNullOrEmptyException();

                            InfoAccessType result = _ceeInfo.ConstructStringLiteral(thisHandle, hModule, metadataToken, ppValue);

                            IntPtr pEntry = Marshal.ReadIntPtr(ppValue);

                            IntPtr objectHandle = Marshal.ReadIntPtr(pEntry);
                            IntPtr hashMapPtr = Marshal.ReadIntPtr(objectHandle);

                            byte[] newContent = Encoding.Unicode.GetBytes(context.Content);

                            objectHandle = Marshal.AllocHGlobal(IntPtr.Size + sizeof(int) + newContent.Length);

                            Marshal.WriteIntPtr(objectHandle, hashMapPtr);
                            Marshal.WriteInt32(objectHandle + IntPtr.Size, newContent.Length / 2);
                            Marshal.Copy(newContent, 0, objectHandle + IntPtr.Size + sizeof(int), newContent.Length);

                            Marshal.WriteIntPtr(pEntry, objectHandle);

                            return result;
                        }
                    }
                }

                return _ceeInfo.ConstructStringLiteral(thisHandle, hModule, metadataToken, ppValue);
            }
            finally
            {
                _tokenTls.EnterCount--;
            }
        }

        public void Dispose()
        {
            lock (JitLock)
            {
                if (_isDisposed)
                    return;

                _hookManager.RemoveHook(_resolveToken);
                _hookManager.RemoveHook(_compileMethod);
                _hookManager.RemoveHook(_constructStringLiteral);

                _methodResolvers = null;
                _tokenResolvers = null;
                _constructStringLiteral = null;

                _compileMethod = null;
                _resolveToken = null;
                _constructStringLiteral = null;

                _instance = null;
                _isDisposed = true;
            }

            GC.SuppressFinalize(this);
        }

    }
}