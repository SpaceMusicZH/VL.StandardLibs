﻿#nullable enable
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VL.Core;
using VL.Core.Import;
using VL.Core.Logging;
using VL.Lib.Animation;
using VL.Lib.Reactive;
using VL.Serialization.MessagePack;
using VL.Serialization.Raw;

[assembly: ImportAsIs(Namespace = "VL")]

namespace VL.IO.Redis
{
    [ProcessNode(HasStateOutput = true)]
    public class RedisClient : IDisposable
    {
        private readonly CompositeDisposable _disposables = new();
        private readonly TransactionBuilder _transactionBuilder = new();
        private readonly IFrameClock _frameClock;
        private readonly ILoggerFactory? _loggerFactory;
        private readonly ILogger _logger;
        private readonly Dictionary<string, (BindingModel model, IDisposable binding)> _bindings = new();

        private ImmutableArray<IParticipant> _participants = ImmutableArray<IParticipant>.Empty;

        private ConnectionMultiplexer? _multiplexer;
        private CancellationTokenSource? _cancellationTokenSource;

        private Task? _lastTransaction;
        private string? _configuration;

        public RedisClient(IFrameClock frameClock, ILoggerFactory? loggerFactory)
        {
            _frameClock = frameClock;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory?.CreateLogger<RedisClient>() ?? NullLogger<RedisClient>.Instance;

            frameClock.GetTicks()
                .Subscribe(BeginFrame)
                .DisposeBy(_disposables);

            frameClock.GetFrameFinished()
                .Subscribe(EndFrame)
                .DisposeBy(_disposables);
        }

        public void Dispose()
        {
            _disposables.Dispose();
            Disconnect();
        }

        [Fragment(Order = int.MinValue)]
        public void Update(string? configuration = "localhost:6379", Action<ConfigurationOptions>? configure = null)
        {
            if (_configuration == configuration)
                return;

            _configuration = configuration;

            var options = new ConfigurationOptions();
            if (configuration != null)
                options = ConfigurationOptions.Parse(configuration);
            if (configure != null)
                options.Apply(configure);

            Reconnect(options);
        }

        [DefaultValue(SerializationFormat.MessagePack)]
        public SerializationFormat Format { private get; set; } = SerializationFormat.MessagePack;

        [DefaultValue(-1)]
        public int Database { internal get; set; } = -1;

        internal IFrameClock FrameClock => _frameClock;

        internal IDisposable Subscribe(IParticipant participant)
        {
            _participants = _participants.Add(participant);
            return Disposable.Create(() => _participants = _participants.Remove(participant));
        }

        internal (ISubscriber? subscriber, CancellationToken cancellationToken) GetSubscriber()
        {
            if (_multiplexer is null)
                return default;

            var subscriber = _multiplexer.GetSubscriber();
            var token = _cancellationTokenSource!.Token;
            return (subscriber, token);
        }

        internal IDisposable AddBinding(BindingModel model, IChannel channel)
        {
            if (_bindings.ContainsKey(model.Key))
                throw new InvalidOperationException($"The redis key \"{model.Key}\" is already bound to a different channel.");

            var binding = (IDisposable)Activator.CreateInstance(
                type: typeof(Binding<>).MakeGenericType(channel.ClrTypeOfValues),
                args: new object[] { this, channel, model })!;
            _bindings[model.Key] = (model, binding);
            return Disposable.Create(() =>
            {
                _bindings.Remove(model.Key);
                binding.Dispose();
            });
        }

        internal RedisValue Serialize<T>(T? value, SerializationFormat? preferredFormat)
        {
            switch (GetEffectiveSerializationFormat(preferredFormat))
            {
                case SerializationFormat.Raw:
                    return RawSerialization.Serialize(value);
                case SerializationFormat.MessagePack:
                    return MessagePackSerialization.Serialize(value);
                case SerializationFormat.Json:
                    return MessagePackSerialization.SerializeJson(value);
                default:
                    throw new NotImplementedException();
            }
        }

        internal T? Deserialize<T>(RedisValue redisValue, SerializationFormat? preferredFormat)
        {
            switch (GetEffectiveSerializationFormat(preferredFormat))
            {
                case SerializationFormat.Raw:
                    return RawSerialization.Deserialize<T>(redisValue);
                case SerializationFormat.MessagePack:
                    return MessagePackSerialization.Deserialize<T>(redisValue);
                case SerializationFormat.Json:
                    return MessagePackSerialization.DeserializeJson<T>(redisValue.ToString());
                default:
                    throw new NotImplementedException();
            }
        }

        SerializationFormat GetEffectiveSerializationFormat(SerializationFormat? preferredFormat) => preferredFormat ?? Format;

        private void Connect(ConfigurationOptions options)
        {
            try
            {
                options.LoggerFactory ??= _loggerFactory;
                options.Protocol = RedisProtocol.Resp2;
                // Attach our unique id so we can identify our pub/sub connection later (see below)
                options.ClientName = $"{options.ClientName ?? options.Defaults.ClientName}{GetHashCode()}";
                // Needed to get the client list, see bwloe
                options.AllowAdmin = true;

                _multiplexer = ConnectionMultiplexer.Connect(options);
                _cancellationTokenSource = new CancellationTokenSource();

                // HACK: It seems the StackExchange API is a little too high level here / doesn't support this yet properly:
                // 1) CLIENT TRACKING ON without the REDIRECT option requires RESP3, but StackExchange will crash in that case not being able to handle the incoming server message
                // 2) CLIENT TRACKING ON with the REDIRECT option only seems to work in RESP2, but in RESP2 we need to use a 2nd connection for Pub/Sub.
                //    However getting the ID of that 2nd connection is not possible in StackExchange (https://stackoverflow.com/questions/66964604/how-do-i-get-the-client-id-for-the-isubscriber-connection)
                //    we need to ask the server (requires the AllowAdmin option) for the client list and then identify our pub/sub connection.
                // Hopefully the situation should improve once https://github.com/StackExchange/StackExchange.Redis/tree/server-cache-invalidation is merged back.

                // This opens a Pub/Sub connection internally
                var (subscriber, token) = GetSubscriber();
                var _invalidations = subscriber!.Subscribe(RedisChannel.Literal("__redis__:invalidate"));
                _invalidations.OnMessage(OnInvalidationMessage);
                token.Register(() => _invalidations.Unsubscribe(CommandFlags.FireAndForget));

                // Let's try to find that one now
                foreach (var s in _multiplexer.GetServers())
                {
                    var pubSubClient = s.ClientList().FirstOrDefault(c => c.Name == _multiplexer.ClientName && c.ClientType == ClientType.PubSub);
                    if (pubSubClient != null)
                    {
                        s.Execute("CLIENT", new object[] { "TRACKING", "ON", "REDIRECT", pubSubClient.Id.ToString(), "BCAST", "NOLOOP" });
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                _logger?.LogError(e, "Exception while connecting.");
            }
        }

        private void Disconnect()
        {
            try
            {
                if (_cancellationTokenSource != null)
                    _cancellationTokenSource.Cancel();

                if (_multiplexer != null)
                    _multiplexer.Dispose();
            }
            catch (Exception e)
            {
                _logger?.LogError(e, "Exception while disconnecting.");
            }
            finally
            {
                _multiplexer = null;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        private void Reconnect(ConfigurationOptions? options)
        {
            Disconnect();

            if (options != null)
                Connect(options);
        }

        private void OnInvalidationMessage(ChannelMessage message)
        {
            var key = message.Message.ToString();
            if (key is null)
                return;

            if (_logger.IsEnabled(LogLevel.Trace))
                _logger.LogTrace("Redis invalidated {key}", key);

            foreach (var p in _participants)
                p.Invalidate(key);
        }

        private void BeginFrame(FrameTimeMessage _)
        {
            // Make room for the next transaction
            if (_lastTransaction != null)
            {
                if (_lastTransaction.IsCompleted)
                {
                    if (_lastTransaction.IsFaulted)
                    {
                        _logger?.LogError(_lastTransaction.Exception, "Exception in last transaction.");
                        _lastTransaction = null;
                    }

                    _lastTransaction = null;
                }
            }
        }

        private void EndFrame(FrameFinishedMessage _) 
        {
            if (_multiplexer is null)
                return;

            // Do not build a new transaction while another one is still in progress
            if (_lastTransaction != null)
                return;

            // 1) Collect changes and if necessary build a new transaction
            _transactionBuilder.Clear();
            foreach (var participant in _participants)
                participant.BuildUp(_transactionBuilder);

            if (_transactionBuilder.IsEmpty)
                return;

            // 2) Send the transaction
            var database = _multiplexer.GetDatabase(Database);
            _lastTransaction = _transactionBuilder.BuildAndExecuteAsync(database);
        }
    }

    public record struct BindingModel(
        string Key, 
        Initialization Initialization = Initialization.Redis,
        RedisBindingType BindingType = RedisBindingType.SendAndReceive, 
        CollisionHandling CollisionHandling = default, 
        SerializationFormat? SerializationFormat = default);

    sealed class TransactionBuilder
    {
        private readonly List<Func<ITransaction, Task>> _actions = new();
        private readonly List<Task> _tasks = new();

        public bool IsEmpty => _actions.Count == 0;

        public CommandFlags CommandFlags { get; set; }

        public void Add(Func<ITransaction, Task> asyncAction)
        {
            _actions.Add(asyncAction);
        }

        public Task BuildAndExecuteAsync(IDatabase database)
        {
            _tasks.Clear();

            var transaction = database.CreateTransaction();
            foreach (var action in _actions)
                _tasks.Add(action(transaction));
            _tasks.Add(transaction.ExecuteAsync(CommandFlags));

            return Task.WhenAll(_tasks);
        }

        public void Clear()
        {
            _actions.Clear();
            CommandFlags = CommandFlags.None;
        }
    }

    interface IParticipant
    {
        void BuildUp(TransactionBuilder builder);
        void Invalidate(string key);
    }

    [ProcessNode]
    public class Binding : IDisposable
    {
        private readonly SerialDisposable _current = new();
        private readonly NodeContext _nodeContext;

        private (RedisClient? client, IChannel? channel, string? key, Initialization initialization, RedisBindingType bindingType, CollisionHandling collisionHandling, SerializationFormat? serializationFormat) _config;

        public Binding(NodeContext nodeContext)
        {
            _nodeContext = nodeContext;
        }

        public void Update(
            RedisClient? client, 
            IChannel? channel, 
            string? key,
            Initialization initialization = Initialization.Redis,
            RedisBindingType bindingType = RedisBindingType.SendAndReceive,
            CollisionHandling collisionHandling = default,
            SerializationFormat? serializationFormat = default)
        {
            var config = (client, channel, key, initialization, bindingType, collisionHandling, serializationFormat);
            if (config == _config)
                return;

            _config = config;
            _current.Disposable = null;

            if (client is null || channel is null || string.IsNullOrWhiteSpace(key))
                return;

            var model = new BindingModel(key, initialization, bindingType, collisionHandling, serializationFormat);
            try
            {
                _current.Disposable = client.AddBinding(model, channel);
            }
            catch (Exception e)
            {
                // TODO: Use logger / add better API which also works in exported apps
                _current.Disposable = IVLRuntime.Current?.AddException(_nodeContext, e);
            }
        }

        public void Dispose()
        {
            _current.Dispose();
        }
    }

    internal class Binding<T> : IParticipant, IDisposable
    {
        private readonly SerialDisposable _clientSubscription = new();
        private readonly SerialDisposable _channelSubscription = new();
        private readonly string _authorId;
        private readonly RedisClient _client;
        private readonly IChannel<T> _channel;
        private readonly BindingModel _bindingModel;

        private bool _initialized;
        private bool _weHaveNewData;
        private bool _othersHaveNewData;

        public Binding(RedisClient client, IChannel<T> channel, BindingModel bindingModel)
        {
            _client = client;
            _channel = channel;
            _bindingModel = bindingModel;

            _initialized = bindingModel.Initialization == Initialization.None;
            _authorId = this.GetHashCode().ToString();

            _clientSubscription.Disposable = client.Subscribe(this);
            _channelSubscription.Disposable = channel.Subscribe(v =>
            {
                if (_channel.LatestAuthor != _authorId)
                {
                    _weHaveNewData = true;
                }
            });
        }

        public void Dispose()
        {
            _clientSubscription.Dispose();
            _channelSubscription.Dispose();
        }

        void IParticipant.Invalidate(string key)
        {
            if (key == _bindingModel.Key)
                _othersHaveNewData = true;
        }

        void IParticipant.BuildUp(TransactionBuilder builder)
        {
            var needToReadFromDb = NeedToReadFromDb();
            var needToWriteToDb = NeedToWriteToDb();
            if (!needToReadFromDb && !needToWriteToDb)
                return;

            if (needToReadFromDb && needToWriteToDb)
            {
                if (_bindingModel.CollisionHandling == CollisionHandling.LocalWins)
                    needToReadFromDb = false;
                else if (_bindingModel.CollisionHandling == CollisionHandling.RedisWins)
                    needToWriteToDb = false;
            }

            builder.Add(async transaction =>
            {
                _initialized = true;
                _weHaveNewData = false;
                _othersHaveNewData = false;

                var key = _bindingModel.Key;
                if (needToWriteToDb)
                {
                    var redisValue = _client.Serialize(_channel.Value, _bindingModel.SerializationFormat);
                    _ = transaction.StringSetAsync(key, redisValue, flags: CommandFlags.FireAndForget);
                }
                if (needToReadFromDb)
                {
                    var redisValue = await transaction.StringGetAsync(key).ConfigureAwait(false);
                    if (redisValue.HasValue)
                    {
                        var value = _client.Deserialize<T>(redisValue, _bindingModel.SerializationFormat);

                        var networkSync = _client.FrameClock.GetTicks();
                        await networkSync.Take(1);

                        _channel.SetValueAndAuthor(value, author: _authorId);
                    }
                }
            });

            bool NeedToReadFromDb()
            {
                var bindingType = _bindingModel.BindingType;
                if (bindingType == RedisBindingType.AlwaysReceive)
                    return true;
                if (!bindingType.HasFlag(RedisBindingType.Receive))
                    return false;
                if (_initialized)
                    return _othersHaveNewData;
                return _bindingModel.Initialization == Initialization.Redis;
            }

            bool NeedToWriteToDb()
            {
                if (!_bindingModel.BindingType.HasFlag(RedisBindingType.Send))
                    return false;
                if (_initialized)
                    return _weHaveNewData;
                return _bindingModel.Initialization == Initialization.Local;
            }
        }
    }

    // TODO: Fix node name - has stupid `1 inside!
    [ProcessNode]
    public class Publish<T> : IDisposable
    {
        private readonly SerialDisposable _subscription = new();
        private readonly ILogger _logger;

        private (RedisClient? client, string? redisChannel, IObservable<T>? value, RedisChannel.PatternMode pattern, SerializationFormat? format) _config;

        // TODO: For unit testing it would be nice to take the logger directly!
        public Publish(NodeContext nodeContext)
        {
            _logger = nodeContext.GetLogger();
        }

        public void Dispose() 
        {
            _subscription.Dispose();
        }

        public void Update(
            RedisClient? client, 
            string? redisChannel, 
            IObservable<T>? stream, 
            RedisChannel.PatternMode pattern = RedisChannel.PatternMode.Auto, 
            SerializationFormat? serializationFormat = default)
        {
            var config = (client, redisChannel, stream, pattern, serializationFormat);
            if (config == _config)
                return;

            _subscription.Disposable = null;

            if (client is null || redisChannel is null || stream is null)
                return;

            var (subscriber, token) = client.GetSubscriber();
            if (subscriber is null)
                return;

            _config = config;
            var streamSubscription = stream.Subscribe(v =>
            {
                try
                {
                    var channel = new RedisChannel(redisChannel, pattern);
                    var value = client.Serialize(v, serializationFormat);
                    subscriber.Publish(channel, value, CommandFlags.FireAndForget);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Exception while publishing.");
                }
            });
            var cancellationSubscription = token.Register(() => streamSubscription.Dispose());
            _subscription.Disposable = new CompositeDisposable(streamSubscription, cancellationSubscription);
        }
    }
}
