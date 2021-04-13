﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Jitex.Utils.Extension;
using IntPtr = System.IntPtr;

namespace Jitex.Utils
{
    /// <summary>
    /// Helpers to manage delegate
    /// </summary>
    public static class DelegateHelper
    {
        public static IList<Type> CreateParameters(MethodBase method)
        {
            IList<Type> parameters = new List<Type>();

            if (!method.IsStatic)
                parameters.Add(typeof(IntPtr));

            if (method.IsGenericMethod)
                parameters.Add(typeof(IntPtr));

            foreach (ParameterInfo parameter in method.GetParameters())
            {
                Type type = parameter.ParameterType;

                if (type.IsPrimitive)
                    parameters.Add(type);
                else
                    parameters.Add(typeof(IntPtr));
            }

            return parameters;
        }

        public static Delegate BuildDelegate(IntPtr addressMethod, MethodBase method)
        {
            IList<Type> parameters = CreateParameters(method);
            Type[] parametersArray = parameters.ToArray();

            Type retType;
            Type? boxType = null;

            if (method.IsConstructor)
            {
                retType = typeof(void);
            }
            else
            {
                MethodInfo methodInfo = (MethodInfo) method;
                Type returnType = methodInfo.ReturnType;

                if (returnType.IsValueTask() && !methodInfo.IsStatic 
                    && parametersArray.Length > 1 && parametersArray[2].CanBeInline())
                {
                    retType = returnType;
                }
                else if (returnType == typeof(void))
                {
                    retType = typeof(void);
                }
                else if (!returnType.IsPrimitive)
                {
                    boxType = typeof(IntPtr);
                    retType = typeof(object);
                }
                else
                {
                    boxType = methodInfo.ReturnType;
                    retType = typeof(object);
                }
            }

            DynamicMethod dm = new($"{method.Name}Original", retType, parametersArray, method.DeclaringType, true);
            ILGenerator generator = dm.GetILGenerator();

            for (int i = 0; i < parameters.Count; i++)
                generator.Emit(OpCodes.Ldarg, i);

            generator.Emit(OpCodes.Ldc_I8, addressMethod.ToInt64());
            generator.Emit(OpCodes.Conv_I);

            if (method.IsStatic || method.IsConstructor)
                generator.EmitCalli(OpCodes.Calli, CallingConventions.Any, retType, parametersArray, null);
            else
                generator.EmitCalli(OpCodes.Calli, CallingConventions.HasThis, retType, parametersArray.Skip(1).ToArray(), null);
            
            if (boxType != null && retType != typeof(void))
                generator.Emit(OpCodes.Box, boxType);

            generator.Emit(OpCodes.Ret);

            Type delegateType;

            if (retType == typeof(void))
            {
                delegateType = Expression.GetActionType(parameters.ToArray());
            }
            else
            {
                parameters.Add(retType);
                delegateType = Expression.GetFuncType(parameters.ToArray());
            }

            return dm.CreateDelegate(delegateType);
        }

        public static Delegate CreateDelegate(IntPtr address, MethodBase method)
        {
            return BuildDelegate(address, method);
        }
    }
}