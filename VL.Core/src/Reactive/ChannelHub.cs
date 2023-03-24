﻿using System;
using VL.Lib.Reactive;
using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Reactive.Subjects;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;

#nullable enable

namespace VL.Core.Reactive
{
    /// <summary>
    /// A ChannelHub manages the lifetime of channels.
    /// 
    /// Channels & Bindings:
    /// The user shall be able to edit global channels manually in an editor extension window.
    /// It shall be possible to remove channels, even if some come with a couple of bindings. Those should get deleted as well.
    ///     Bindings could get stored as components attached to a channel.
    ///     A Redis app component would only store which channels to sync (their names e.g. via a regular expression query and look 
    ///     up the Redis bindings in the found channels in the ChannelHub).
    /// 
    /// Entry points:
    /// A user wants to think per entry point. 
    /// Reasoning: 
    ///     Opening help patches shall not mess up your project entry point; 
    ///     After closing a patch the dev session should be as clean as possible..
    /// Per entry point we have one channel hub that represents all global channels for the app. 
    /// There still could be a way to opt-into a global channel behavior that binds channels of different entry points:
    ///     a certain app component that bridges one entry-point with the other (merges their ChannelHubs on localhost).
    /// 
    /// IDE & imperative API:
    /// Editor extension windows (for editing global channels and their bindings) show the global channels
    /// for one entry point - which could be synced to what patch we currently look at. There could be different
    /// such windows that all want to help the user from different angels (MIDI specific view, overview over 
    /// all channels and their bindings...). 
    ///     In order to implement those windows the channel hub has an imperative way to add and remove or modify channels. 
    /// 
    /// Descriptive API:
    /// An alternative design is to have channel description providers, which can register and tell of their needs via
    /// something like a IObservable<IEnumerable<ChannelDescription>>. The distinct, merged needs of all channel 
    /// providers would determine which channels exist. This could be added later. 
    ///     All those channels could be locked; they wouldn't be editable by the user, since those are needed by some app component.
    ///     When adding an IOBoxSyncAppComponent this could be thought of as a collection of channeldescriptions, resulting in locked channels
    /// 
    /// vvvv Editor ChannelHub:
    /// The vvvv editor runtime can be seen as yet another app, which comes with its own channels hub.
    /// A boygroup app component e.g. would be part of the vvvv IDE, which shall work even when in paused or stopped mode.
    /// Such a boygrouping app component would setup a Redis binding for some central channels inside the editor channel hub.
    ///     A channel Boygrouping/DocDiffs is not visible to the user as it is part of the IDE channelhub, not an entry-point channelhub.
    /// 
    /// Usage & Storage:
    /// For the user app the user shall place a GlobalChannels node somewhere. 
    /// That's the place where to store the channel descriptions in a pin. 
    /// This way we don't need to invent config files or hidden areas in a vl document to store those settings. 
    /// The user is boss where to place this central node and how to reference the file in question.
    /// Ideally the patcher would only get access to the global channels when there is a reference to the document that 
    /// has setup those channels. 
    /// 
    /// Accessing channels:
    /// Access to channels can be granted by statically typed nodes - named after the channel - and by dynamic lookups.
    /// Whenever the ChannelHub for an entry point changed its entries new nodes get built by a factory accordingly. 
    /// BeginChange can be used to group changes in bulks, so that the event OnChannelsChanged triggers less often.
    /// The ChannelHub implementation takes care of creating the nodes.
    /// 
    /// Implementation:
    /// The GlobalChannels node would aquire the IChannelHub app component per app via IAppHost.GetComponent;
    /// And then take control over it, representing the single source of truth for the channels for this app.
    /// App Components (e.g. Redis) are managed by the apphost. It disposes all app components on Stop.
    /// They can and should make use of this moment and leave a clean state. E.g. remove certain channels from a Redis database.
    /// </summary>
    public interface IChannelHub
    {
        IDictionary<string, Channel> Channels { get; }

        Channel? TryGetChannel(string key);
        
        Channel? TryAddChannel(string key, Type typeOfValues);

        bool TryRemoveChannel(string key);

        Channel? TryRenameChannel(string key, string newKey);

        Channel? TryChangeType(string key, Type typeOfValues);        

        /// <summary>
        /// Do several changes to the ChannelHub in one go. using BeginChange()
        /// </summary>
        /// <returns></returns>
        IDisposable BeginChange();

        IObservable<object> OnChannelsChanged {  get; }

        void BatchUpdate(Action<IChannelHub> action)
        {
            using var _ = BeginChange();
            action(this);
        }

        static IChannelHub HubForApp => IAppHost.GetAppComponent<IChannelHub>();
    }

    //public interface IChannelDescriptionProvider
    //{
    //    IObservable<IEnumerable<ChannelDescription>> DescribeNeeds();
    //}


    public class ChannelHub : IChannelHub, IDisposable
    {
        int lockCount = 0;
        int revision = 0;
        int revisionOnLockTaken = 0;
        public Subject<object> onChannelsChanged = new Subject<object>();
        public IObservable<object> OnChannelsChanged => onChannelsChanged;


        IDisposable? MustHaveDescriptiveSubscription;
        public IObservable<IEnumerable<ChannelBuildDescription>> MustHaveDescriptive
        {
            set
            {
                MustHaveDescriptiveSubscription?.Dispose();
                MustHaveDescriptiveSubscription = value.Subscribe(descriptions =>
                {
                    ((IChannelHub)this).BatchUpdate(_ =>
                    {
                        // make sure all channels of the descriptive configuration exist.
                        // we don't delete channels that are not listed as the user might have added some more programmatically.
                        // the config only describes those that shall be there on startup.
                        foreach (var d in descriptions)
                            TryAddChannel(d.Name, d.Type);
                    });
                });
            }
        } 

        internal ConcurrentDictionary<string, Channel> Channels = new ConcurrentDictionary<string, Channel>();
        IDictionary<string, Channel> IChannelHub.Channels => Channels;

        public IDisposable BeginChange()
        {
            if (lockCount == 0)
                revisionOnLockTaken = revision;
            lockCount++;
            return Disposable.Create(EndChange);
        }

        void EndChange()
        {
            lockCount--;
            if (lockCount == 0 && revisionOnLockTaken != revision)
                onChannelsChanged.OnNext(this);
        }

        public Channel? TryAddChannel(string key, Type typeOfValues)
        {
            using var _ = BeginChange();
            var c = Channels.GetOrAdd(key, _ => { var c = Channel.CreateChannelOfType(typeOfValues); revision++; return c; });
            if (c.ClrTypeOfValues != typeOfValues)
                return default;
            // discuss if replacing with new type is an option or should always occur.
            // for now never replacing. User of API shall call TryChangeType for now.
            return c;
        }

        public Channel? TryGetChannel(string key)
        {
            Channels.TryGetValue(key, out var c);
            return c;
        }

        public bool TryRemoveChannel(string key)
        {
            using var _ = BeginChange();
            var gotRemoved = Channels.TryRemove(key, out var c);
            if (c != null)
            {
                revision++;
                c.Dispose();// might not really be necessary, but let's clean up for now. We are at least the ones who created the channels.
            }
            return gotRemoved;
        }

        public Channel? TryRenameChannel(string key, string newKey)
        {
            using var _ = BeginChange();
            var gotRemoved = Channels.TryRemove(key, out var c);
            if (c != null)
            {
                revision++;
                c.Dispose();
                return TryAddChannel(newKey, c.ClrTypeOfValues);
            }
            return null;
        }

        public Channel? TryChangeType(string key, Type typeOfValues)
        {
            using var _ = BeginChange();
            var gotRemoved = Channels.TryRemove(key, out var c);
            if (c != null)
            {
                revision++;
                c.Dispose();
                return TryAddChannel(key, typeOfValues);
            }
            return null;
        }

        public void Dispose()
        {
            {
                using var _ = BeginChange();
                var cs = Channels.Values;
                Channels.Clear();
                revision++;
                foreach (var c in cs)
                    c.Dispose();
            }
            onChannelsChanged.Dispose();
            MustHaveDescriptiveSubscription?.Dispose();
        }
    }
}

#nullable restore