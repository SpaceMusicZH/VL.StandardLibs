﻿using CommunityToolkit.HighPerformance;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using VL.Core.Utils;
using VL.Lib.Collections;


namespace VL.MessagePack
{
    public static class BytesExtensions
    {
        public static void ToBytes<T>(T input, out ReadOnlyMemory<byte> bytes, out bool successful)
        {
            if (input is null)
            {
                bytes = default;
                successful = false;
                return;
            }

            var ToMemoryMap = BytesExtensions.ToMemoryMap.Value;
            if (ToMemoryMap.TryGetValue(typeof(T), out Func<object, ReadOnlyMemory<byte>>? ToDelegate))
            {
                bytes = ToDelegate(input);
                successful = true;
                return;
            }
            else
            {
                // Struct, Vector2,Vector3 ...
                if (typeof(T).IsBlitable())
                {
                    var blitableToBytes = typeof(BytesExtensions).GetMethod(nameof(BlitableToBytes), BindingFlags.NonPublic | BindingFlags.Static);

                    if (blitableToBytes != null)
                    {
                        blitableToBytes = blitableToBytes.MakeGenericMethod(new[] { typeof(T) });
                        var BlitableToBytesDel = StaticMethodDelegate<object, ReadOnlyMemory<byte>>(blitableToBytes);
                        ToMemoryMap.Add(typeof(T), BlitableToBytesDel);

                        bytes = BlitableToBytesDel(input);
                        successful = true;
                        return;
                    }
                }
                // Spread of Struct 
                else if (typeof(ISpread).IsAssignableFrom(typeof(T)))
                {
                    if (typeof(T).IsGenericType)
                    {
                        var genericType = typeof(T).GetGenericArguments()[0];
                        if (genericType != null && genericType.IsBlitable())
                        {
                            var spreadToBytes = typeof(BytesExtensions).GetMethod(nameof(SpreadToBytes), BindingFlags.NonPublic | BindingFlags.Static);
                            if (spreadToBytes != null)
                            {
                                spreadToBytes = spreadToBytes.MakeGenericMethod(new[] { genericType });
                                var SpreadToBytesDel = StaticMethodDelegate<object, ReadOnlyMemory<byte>>(spreadToBytes);
                                ToMemoryMap.Add(typeof(T), SpreadToBytesDel);

                                bytes = SpreadToBytesDel(input);
                                successful = true;
                                return;
                            }
                        }
                    }
                }
                // ImmutableArray
                else if (typeof(T).Name.StartsWith("ImmutableArray"))
                {
                    if (typeof(T).IsGenericType)
                    {
                        var genericType = typeof(T).GetGenericArguments()[0];
                        if (genericType != null && genericType.IsBlitable())
                        {
                            var immutableArrayToBytes = typeof(BytesExtensions).GetMethod(nameof(ImmutableArrayToBytes), BindingFlags.NonPublic | BindingFlags.Static);
                            if (immutableArrayToBytes != null)
                            {
                                immutableArrayToBytes = immutableArrayToBytes.MakeGenericMethod(new[] { genericType });
                                var ImmutableArrayToBytesDel = StaticMethodDelegate<object, ReadOnlyMemory<byte>>(immutableArrayToBytes);
                                ToMemoryMap.Add(typeof(T), ImmutableArrayToBytesDel);

                                bytes = ImmutableArrayToBytesDel(input);
                                successful = true;
                                return;
                            }
                        }
                    }

                }
                // Array
                else if (typeof(T).IsArray && typeof(T).HasElementType)
                {
                    var genericType = typeof(T).GetElementType();
                    if (genericType != null && genericType.IsBlitable())
                    {
                        var arrayToBytes = typeof(BytesExtensions).GetMethod(nameof(ArrayToBytes), BindingFlags.NonPublic | BindingFlags.Static);
                        if (arrayToBytes != null)
                        {
                            arrayToBytes = arrayToBytes.MakeGenericMethod(new[] { genericType });
                            var ArrayToBytesDel = StaticMethodDelegate<object, ReadOnlyMemory<byte>>(arrayToBytes);
                            ToMemoryMap.Add(typeof(T), ArrayToBytesDel);

                            bytes = ArrayToBytesDel(input);
                            successful = true;
                            return;
                        }
                    }
                }
                // String
                else if (typeof(T) == typeof(String))
                {
                    Func<object, ReadOnlyMemory<byte>> StringToBytesDel = (obj) => StringToBytes((string)obj);
                    ToMemoryMap.Add(typeof(T), StringToBytesDel);

                    bytes = StringToBytesDel(input);
                    successful = true;
                    return;
                }
            }

            bytes = default;
            successful = false;
            return;
        }

        public static void FromBytes<T>(ReadOnlyMemory<byte> bytes, out T? output, out bool successful)
        {
            var FromMemoryMap = BytesExtensions.FromMemoryMap.Value;
            if (FromMemoryMap.TryGetValue(typeof(T), out Func<ReadOnlyMemory<byte>, object>? FromDelegate))
            {
                output = (T)FromDelegate(bytes);
                successful = true;
                return;
            }
            else
            {
                // Struct, Vector2,Vector3 ...
                if (typeof(T).IsBlitable())
                {
                    var bytesToBlitable = typeof(BytesExtensions).GetMethod(nameof(BytesToBlitable), BindingFlags.NonPublic | BindingFlags.Static);

                    if (bytesToBlitable != null)
                    {
                        bytesToBlitable = bytesToBlitable.MakeGenericMethod(new[] { typeof(T) });

                        var BytesToBlitableDel = StaticMethodDelegate<ReadOnlyMemory<byte>, object>(bytesToBlitable);
                        FromMemoryMap.Add(typeof(T), BytesToBlitableDel);

                        output = (T)BytesToBlitableDel(bytes);
                        successful = true;
                        return;
                    }
                }
                // Spread of Struct 
                else if (typeof(ISpread).IsAssignableFrom(typeof(T)))
                {
                    if (typeof(T).IsGenericType)
                    {
                        var genericType = typeof(T).GetGenericArguments()[0];
                        if (genericType != null && genericType.IsBlitable())
                        {
                            var bytesToSpread = typeof(BytesExtensions).GetMethod(nameof(BytesToSpread), BindingFlags.NonPublic | BindingFlags.Static);
                            if (bytesToSpread != null)
                            {
                                bytesToSpread = bytesToSpread.MakeGenericMethod(new[] { genericType });
                                var BytesToSpreadDel = StaticMethodDelegate<ReadOnlyMemory<byte>, object>(bytesToSpread);
                                FromMemoryMap.Add(typeof(T), BytesToSpreadDel);

                                output = (T)BytesToSpreadDel(bytes);
                                successful = true;
                                return;
                            }
                        }
                    }
                }
                // ImmutableArray
                else if (typeof(T).Name.StartsWith("ImmutableArray"))
                {
                    if (typeof(T).IsGenericType)
                    {
                        var genericType = typeof(T).GetGenericArguments()[0];
                        if (genericType != null && genericType.IsBlitable())
                        {
                            var bytesToImmutableArray = typeof(BytesExtensions).GetMethod(nameof(BytesToImmutableArray), BindingFlags.NonPublic | BindingFlags.Static);
                            if (bytesToImmutableArray != null)
                            {
                                bytesToImmutableArray = bytesToImmutableArray.MakeGenericMethod(new[] { genericType });
                                var BytesToImmutableArrayDel = StaticMethodDelegate<ReadOnlyMemory<byte>, object>(bytesToImmutableArray);
                                FromMemoryMap.Add(typeof(T), BytesToImmutableArrayDel);

                                output = (T)BytesToImmutableArrayDel(bytes);
                                successful = true;
                                return;
                            }
                        }
                    }
                }
                // Array
                else if (typeof(T).IsArray && typeof(T).HasElementType)
                {
                    var genericType = typeof(T).GetElementType();
                    if (genericType != null && genericType.IsBlitable())
                    {
                        var bytesToArray = typeof(BytesExtensions).GetMethod(nameof(BytesToArray), BindingFlags.NonPublic | BindingFlags.Static);
                        if (bytesToArray != null)
                        {
                            bytesToArray = bytesToArray.MakeGenericMethod(new[] { genericType });
                            var BytesToArrayDel = StaticMethodDelegate<ReadOnlyMemory<byte>, object>(bytesToArray);
                            FromMemoryMap.Add(typeof(T), BytesToArrayDel);

                            output = (T)BytesToArrayDel(bytes);
                            successful = true;
                            return;
                        }
                    }
                }
                // String
                else if (typeof(T) == typeof(String))
                {
                    Func<ReadOnlyMemory<byte>, object> BytesToStringDel = (b) => BytesToString(b);
                    FromMemoryMap.Add(typeof(T), BytesToStringDel);

                    output = (T)BytesToStringDel(bytes);
                    successful = true;
                    return;
                }
            }

            output = default;
            successful = false;
            return;
        }

        private static readonly Lazy<Dictionary<Type, Func<object, ReadOnlyMemory<byte>>>> ToMemoryMap = new Lazy<Dictionary<Type, Func<object, ReadOnlyMemory<byte>>>>(() => new Dictionary<Type, Func<object, ReadOnlyMemory<byte>>>());
        private static readonly Lazy<Dictionary<Type, Func<ReadOnlyMemory<byte>, object>>> FromMemoryMap = new Lazy<Dictionary<Type, Func<ReadOnlyMemory<byte>, object>>>(() => new Dictionary<Type, Func<ReadOnlyMemory<byte>, object>>());

        private static Func<Tinput, TOutput> StaticMethodDelegate<Tinput, TOutput>(MethodInfo method)
        {
            var parameter = method.GetParameters().Single();
            var argument = Expression.Parameter(typeof(Tinput), "argument");
            var methodCall = Expression.Call(
                null,
                method,
                Expression.Convert(argument, parameter.ParameterType)
                );
            return Expression.Lambda<Func<Tinput, TOutput>>(
                Expression.Convert(methodCall, typeof(TOutput)),
                argument
                ).Compile();
        }

        private static ReadOnlyMemory<byte> BlitableToBytes<T>(T input) where T : unmanaged
        {
            return ReadOnlyMemoryExtensions.AsBytes(new ReadOnlyMemory<T>(new T[] { input }));
        }

        private unsafe static T BytesToBlitable<T>(ReadOnlyMemory<byte> input) where T : unmanaged
        {
            T result;
            input.Span.CopyTo(new Span<byte>(&result, sizeof(T)));
            return result;
        }

        private static ReadOnlyMemory<byte> SpreadToBytes<T>(Spread<T> input) where T : unmanaged
        {
            return ReadOnlyMemoryExtensions.Cast<T, byte>(input.AsMemory());
        }
        private static Spread<T> BytesToSpread<T>(ReadOnlyMemory<byte> input) where T : unmanaged
        {
            var span = MemoryMarshal.Cast<byte, T>(input.Span);
            var builder = Pooled.GetArrayBuilder<T>();
            builder.Value.Count = span.Length;
            span.CopyTo(builder.Value.AsSpan());
            return Spread.Create<T>(builder.ToImmutableAndFree());
        }

        private static ReadOnlyMemory<byte> ImmutableArrayToBytes<T>(ImmutableArray<T> input) where T : unmanaged
        {
            return ReadOnlyMemoryExtensions.Cast<T, byte>(input.AsMemory());
        }
        private static ImmutableArray<T> BytesToImmutableArray<T>(ReadOnlyMemory<byte> input) where T : unmanaged
        {
            var span = MemoryMarshal.Cast<byte, T>(input.Span);
            var builder = Pooled.GetArrayBuilder<T>();
            builder.Value.Count = span.Length;
            span.CopyTo(builder.Value.AsSpan());
            return builder.ToImmutableAndFree();
        }
        private static ReadOnlyMemory<byte> ArrayToBytes<T>(T[] input) where T : unmanaged
        {
            return ReadOnlyMemoryExtensions.Cast<T, byte>(input.AsMemory());
        }
        private static T[] BytesToArray<T>(ReadOnlyMemory<byte> input) where T : unmanaged
        {
            var span = MemoryMarshal.Cast<byte, T>(input.Span);
            T[] values = new T[span.Length];
            span.CopyTo(values.AsSpan());
            return values;
        }

        private static ReadOnlyMemory<byte> StringToBytes(string input)
        {
            return new ReadOnlyMemory<byte>(UTF8Encoding.UTF8.GetBytes(input));
        }

        private static string BytesToString(ReadOnlyMemory<byte> input)
        {
            return UTF8Encoding.UTF8.GetString(input.Span);
        }
    }
}
