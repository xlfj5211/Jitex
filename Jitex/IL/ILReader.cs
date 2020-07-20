﻿using Jitex.IL.Resolver;
using Jitex.Utils;
using Jitex.Utils.Extensions;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace Jitex.IL
{
    public class ILReader : IEnumerable<Operation>
    {
        private readonly bool _forceTypeOnGeneric;

        /// <summary>
        ///     Instructions IL.
        /// </summary>
        private readonly byte[] _il;

        private readonly ITokenResolver _resolver;

        public ILReader(MethodBase methodBase)
        {
            _il = methodBase.GetILBytes();

            if (methodBase is DynamicMethod dynamicMethod)
                _resolver = new DynamicMethodTokenResolver(dynamicMethod);
            else
                _resolver = new ModuleTokenResolver(methodBase.Module);
        }

        /// <summary>
        ///     Create a new instance of ILReader.
        /// </summary>
        /// <param name="il">Instructions to read.</param>
        /// <param name="module">Module of instructions.</param>
        /// <param name="forceTypeOnGeneric">Force read type generic.</param>
        public ILReader(byte[] il, Module module, bool forceTypeOnGeneric = true)
        {
            _il = il;
            _forceTypeOnGeneric = forceTypeOnGeneric;

            if (module != null)
                _resolver = new ModuleTokenResolver(module);
        }

        public IEnumerator<Operation> GetEnumerator()
        {
            return new ILEnumerator(_il, _resolver, _forceTypeOnGeneric);
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        /// <summary>
        ///     Enumerator to read instructions.
        /// </summary>
        private class ILEnumerator : IEnumerator<Operation>
        {
            private readonly bool _forceTypeOnGeneric;

            /// <summary>
            ///     Instructions IL.
            /// </summary>
            private readonly byte[] _il;

            private readonly ITokenResolver _resolver;

            private int _index;

            /// <summary>
            ///     Current position of read.
            /// </summary>
            private int _position;

            /// <summary>
            ///     Current operation.
            /// </summary>
            public Operation Current => ReadNextOperation();

            /// <summary>
            ///     Current operation.
            /// </summary>
            object IEnumerator.Current => Current;

            /// <summary>
            ///     Create a new enumerator to read instructions.
            /// </summary>
            /// <param name="il">Instructions to read.</param>
            /// <param name="module">Module of instructions.</param>
            /// <param name="forceTypeOnGeneric">Force read token from generic method</param>
            public ILEnumerator(byte[] il, ITokenResolver resolver, bool forceTypeOnGeneric)
            {
                _il = il;
                _resolver = resolver;
                _forceTypeOnGeneric = forceTypeOnGeneric;
            }

            public void Dispose()
            {
                //throw new NotImplementedException();
            }

            public bool MoveNext()
            {
                return _position < _il.Length;
            }

            /// <summary>
            ///     Read next operation from IL.
            /// </summary>
            /// <returns>The next operation.</returns>
            private Operation ReadNextOperation()
            {
                Operation operation = null;

                int ilIndex = _position;
                OpCode opCode = Operation.Translate(_il[_position++]);

                switch (opCode.OperandType)
                {
                    case OperandType.InlineI8:
                        operation = new Operation(opCode, ReadInt64());
                        break;

                    case OperandType.InlineR:
                        operation = new Operation(opCode, ReadDouble());
                        break;

                    case OperandType.InlineField:
                        (FieldInfo Field, int Token) field = ReadField();
                        operation = new Operation(opCode, field.Field, field.Token);
                        break;

                    case OperandType.InlineMethod:
                        var method = ReadMethod();
                        operation = new Operation(opCode, method.Method, method.Token);
                        break;

                    case OperandType.InlineString:
                        var @string = ReadString();
                        operation = new Operation(opCode, @string.String, @string.Token);
                        break;

                    case OperandType.InlineType:
                        var type = ReadType();
                        operation = new Operation(opCode, type.Type, type.Token);
                        break;

                    case OperandType.InlineI:
                        operation = new Operation(opCode, ReadInt32());
                        break;

                    case OperandType.InlineSig:
                        var signature = ReadSignature();
                        operation = new Operation(opCode, signature.Signature, signature.Token);
                        break;

                    case OperandType.InlineTok:
                        try
                        {
                            var member = ReadMember();
                            operation = new Operation(opCode, member.Member, member.Token);
                        }
                        catch (ArgumentException)
                        {
                            if (_forceTypeOnGeneric)
                            {
                                throw;
                            }

                            _position -= 4;
                            operation = new Operation(opCode, ReadInt32());
                        }

                        break;

                    case OperandType.InlineBrTarget:
                        operation = new Operation(opCode, ReadInt32() + _position);
                        break;

                    case OperandType.ShortInlineR:
                        operation = new Operation(opCode, ReadSingle());
                        break;

                    case OperandType.InlineVar:
                        _position += 2;
                        break;

                    case OperandType.ShortInlineBrTarget: //Repeat jump from original IL.
                    case OperandType.ShortInlineI:
                        if (opCode == OpCodes.Ldc_I4_S)
                            operation = new Operation(opCode, (sbyte) ReadByte());
                        else
                            operation = new Operation(opCode, ReadByte());
                        break;

                    case OperandType.ShortInlineVar:
                        operation = new Operation(opCode, ReadByte());
                        break;

                    case OperandType.InlineSwitch:
                        int length = ReadInt32();
                        int[] branches = new int[length];

                        for (int i = 0; i < length; i++)
                            branches[i] = ReadInt32();

                        operation = new Operation(opCode, branches);
                        break;

                    case OperandType.InlinePhi:
                        break;

                    default:
                        operation = new Operation(opCode, null);
                        break;
                }

                operation.Index = _index++;
                operation.ILIndex = ilIndex;
                operation.Size = _position - ilIndex;
                return operation;
            }

            public void Reset()
            {
                _position = 0;
            }

            #region ReadTypes

            /// <summary>
            ///     Read <see cref="Type" /> reference from module.
            /// </summary>
            /// <returns><see cref="Type" /> referenced.</returns>
            private (Type Type, int Token) ReadType()
            {
                int token = ReadInt32();
                
                if (_resolver == null)
                    return (null, token);
                
                Type type = _resolver.ResolveType(token);
                return (type, token);
            }

            /// <summary>
            ///     Read <see cref="string" /> reference from module.
            /// </summary>
            /// <returns><see cref="string" /> referenced.</returns>
            private (string String, int Token) ReadString()
            {
                int token = ReadInt32();
                
                if (_resolver == null)
                    return (null, token);
                
                return (_resolver.ResolveString(token), token);
            }

            /// <summary>
            ///     Read <see cref="MethodInfo" /> reference from module.
            /// </summary>
            /// <returns><see cref="MethodInfo" /> referenced.</returns>
            private (MethodBase Method, int Token) ReadMethod()
            {
                int token = ReadInt32();
                
                if (_resolver == null)
                    return (null, token);
                
                MethodBase method = _resolver.ResolveMethod(token);
                return (method, token);
            }

            /// <summary>
            ///     Read <see cref="ConstructorInfo" /> reference from module.
            /// </summary>
            /// <returns><see cref="ConstructorInfo" /> referenced.</returns>
            private (ConstructorInfo Constructor, int Token) ReadConstructor()
            {
                int token = ReadInt32();
                
                if (_resolver == null)
                    return (null, token);
                
                ConstructorInfo constructor = (ConstructorInfo) _resolver.ResolveMethod(token);
                return (constructor, token);
            }

            /// <summary>
            ///     Read <see cref="FieldInfo" /> reference from module.
            /// </summary>
            /// <returns><see cref="FieldInfo" /> referenced.</returns>
            private (FieldInfo Field, int Token) ReadField()
            {
                int token = ReadInt32();

                if (_resolver == null)
                    return (null, token);

                FieldInfo field = _resolver.ResolveField(token);
                return (field, token);
            }

            /// <summary>
            ///     Read Signature reference from module.
            /// </summary>
            /// <returns></returns>
            private (byte[] Signature, int Token) ReadSignature()
            {
                int token = ReadInt32();

                if (_resolver == null)
                    return (null, token);
                
                byte[] signature = _resolver.ResolveSignature(token);
                return (signature, token);
            }

            /// <summary>
            ///     Read <see cref="MemberInfo" /> reference from module.
            /// </summary>
            /// <returns></returns>
            private (MemberInfo Member, int Token) ReadMember()
            {
                int token = ReadInt32();
                
                if (_resolver == null)
                    return (null, token);
                
                MemberInfo member = _resolver.ResolveMember(token);
                return (member, token);
            }

            /// <summary>
            ///     Read <see cref="long" /> value.
            /// </summary>
            /// <returns><see cref="long" /> value.</returns>
            private long ReadInt64()
            {
                long value = BitConverter.ToInt64(_il, _position);
                _position += 8;
                return value;
            }

            /// <summary>
            ///     Read <see cref="int" /> value.
            /// </summary>
            /// <returns><see cref="int" /> value.</returns>
            private int ReadInt32()
            {
                int value = _il[_position]
                            | (_il[_position + 1] << 8)
                            | (_il[_position + 2] << 16)
                            | (_il[_position + 3] << 24);
                _position += 4;
                return value;
            }

            private double ReadSingle()
            {
                float value = BitConverter.ToSingle(_il, _position);
                _position += 4;
                return value;
            }

            /// <summary>
            ///     Read <see cref="double" /> value.
            /// </summary>
            /// <returns><see cref="double" /> value.</returns>
            private double ReadDouble()
            {
                double value = BitConverter.ToDouble(_il, _position);
                _position += 8;
                return value;
            }

            /// <summary>
            ///     Read <see cref="byte" /> value.
            /// </summary>
            /// <returns><see cref="byte" /> value.</returns>
            private byte ReadByte()
            {
                return _il[_position++];
            }

            #endregion
        }
    }
}