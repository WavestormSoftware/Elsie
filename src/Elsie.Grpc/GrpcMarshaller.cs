using System.Buffers;
using Grpc.Core;

namespace Elsie.Grpc;

/// <summary>
/// Bridges the byte[]-based serialize/deserialize surface to the contextual
/// <see cref="Marshaller{T}.ContextualSerializer"/> / <see cref="Marshaller{T}.ContextualDeserializer"/>
/// (Grpc.Core.Api 2.80 codegen creates marshallers whose byte[]-based Serializer/Deserializer
/// throw NotImplementedException when the contextual overload was used).
/// </summary>
internal static class GrpcMarshaller
{
    public static byte[] Serialize<T>(Marshaller<T> marshaller, T message)
    {
        ArgumentNullException.ThrowIfNull(marshaller);

        var contextual = marshaller.ContextualSerializer;
        if (contextual is not null)
        {
            var context = new ByteArraySerializationContext();
            contextual(message, context);
            return context.Payload;
        }

        return marshaller.Serializer(message);
    }

    public static T Deserialize<T>(Marshaller<T> marshaller, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(marshaller);
        ArgumentNullException.ThrowIfNull(payload);

        var contextual = marshaller.ContextualDeserializer;
        if (contextual is not null)
        {
            return contextual(new ByteArrayDeserializationContext(payload));
        }

        return marshaller.Deserializer(payload);
    }

    private sealed class ByteArraySerializationContext : SerializationContext
    {
        private byte[] _payload = [];
        private ArrayBufferWriter<byte>? _writer;

        public byte[] Payload => _payload;

        public override void Complete(byte[] payload) => _payload = payload ?? throw new ArgumentNullException(nameof(payload));

        public override IBufferWriter<byte> GetBufferWriter() => _writer ??= new ArrayBufferWriter<byte>();

        public override void SetPayloadLength(int length) { }

        public override void Complete()
        {
            if (_writer is not null)
            {
                _payload = _writer.WrittenSpan.ToArray();
            }
        }
    }

    private sealed class ByteArrayDeserializationContext(byte[] payload) : DeserializationContext
    {
        public override int PayloadLength => payload.Length;

        public override byte[] PayloadAsNewBuffer() => payload;

        public override ReadOnlySequence<byte> PayloadAsReadOnlySequence() => new(payload);
    }
}
