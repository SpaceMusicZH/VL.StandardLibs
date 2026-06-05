using MessagePack;
using System;
using System.Collections.Generic;
using VL.Serialization.MessagePack.Resolvers;

namespace VL.Serialization.MessagePack
{
    public static class MessagePackSerialization
    {
        public static byte[] Serialize<T>(T input)
        {
            return MessagePackSerializer.Serialize(input, VLResolver.Options);
        }

        /// <summary>
        /// Non-generic serialize for callers that only hold a boxed value and its runtime <see cref="Type"/>
        /// (e.g. a channel hub handing out <c>IChannel&lt;object&gt;.Object</c> + <c>ClrTypeOfValues</c>).
        /// Routes through the same <see cref="VLResolver"/> formatter chain as <see cref="Serialize{T}(T)"/>,
        /// so the binary shape is identical to the generic path. Added for perf-036 (plan 036 wire contract §1.9a).
        /// </summary>
        public static byte[] Serialize(Type type, object? value)
        {
            return MessagePackSerializer.Serialize(type, value, VLResolver.Options);
        }

        /// <summary>Non-generic counterpart to <see cref="Deserialize{T}(ReadOnlyMemory{byte})"/>.</summary>
        public static object? Deserialize(Type type, ReadOnlyMemory<byte> input)
        {
            return MessagePackSerializer.Deserialize(type, input, VLResolver.Options);
        }

        /// <summary>
        /// Non-generic JSON encode for a boxed value + runtime <see cref="Type"/>. Routes through the binary
        /// formatters then converts to JSON, guaranteeing the same logical shape as the MessagePack path
        /// (plan 036 wire contract §5.1 — JSON baseline must be apples-to-apples with MessagePack).
        /// <paramref name="prettify"/> defaults to <c>false</c> here (unlike the generic overload) so the
        /// JSON byte-count measurement is not inflated by formatting whitespace.
        /// </summary>
        public static string SerializeJson(Type type, object? value, bool prettify = false)
        {
            var bytes = MessagePackSerializer.Serialize(type, value, VLResolver.Options);
            var json = MessagePackSerializer.ConvertToJson(bytes, VLResolver.Options);
            return prettify ? JsonHelper.FormatJson(json) : json;
        }

        public static T Deserialize<T>(IEnumerable<byte> input)
        {
            if (input.TryGetMemory(out var memory))
                return MessagePackSerializer.Deserialize<T>(memory, VLResolver.Options);

            throw new ArgumentException($"Couldn't retrieve memory from {typeof(T)}", nameof(input));
        }

        public static T Deserialize<T>(ReadOnlyMemory<byte> input)
        {
            return MessagePackSerializer.Deserialize<T>(input, VLResolver.Options);
        }

        public static string SerializeJson<T>(T input, bool prettify = true)
        {
            var json = MessagePackSerializer.SerializeToJson(input, VLResolver.Options);
            if (prettify)
                return JsonHelper.FormatJson(json);
            return json;
        }

        public static T DeserializeJson<T>(string input)
        {
            var bytes = MessagePackSerializer.ConvertFromJson(input, VLResolver.Options);
            return Deserialize<T>((ReadOnlyMemory<byte>)bytes);
        }
    }
}
