using GrainInterfaces;
using Orleans;
using System;
using System.Threading.Tasks;
using Grains.Redux;
using System.Reactive.Linq;
using Orleans.Streams;

namespace Grains
{
    [ImplicitStreamSubscription("ActionsFromClient")]
    public class CounterGrain : ReduxGrain<CounterState>, ICounterGrain
    {
        IDisposable timer = null;
        IStreamProvider streamProvider;
        StreamSubscriptionHandle<IAction> actionStreamSubscription;
        IDisposable storeSubscription;
        IAsyncStream<IAction> actionsToClientStream;

        public override async Task OnActivateAsync()
        {
            // Do this first, it initializes the Store!
            await base.OnActivateAsync();

            this.streamProvider = this.GetStreamProvider("Default");
            var actionsFromClientStream = streamProvider.GetStream<IAction>(this.GetPrimaryKey(), "ActionsFromClient");
            // Subscribe to Actions streamed from the client, and process them.
            // These actions can't be directly dispatched, they need to be interpreted and can cause other actions to be dispatched
            actionStreamSubscription = await actionsFromClientStream.SubscribeAsync(async (action, st) => {
                await this.Process(action);
            });

            this.actionsToClientStream = this.streamProvider.GetStream<IAction>(this.GetPrimaryKey(), "ActionsToClient");

            // Subscribe to state updates as they happen on the server, and publish them using the SyncCounterState action
            this.storeSubscription = this.Store.Subscribe(
                async (CounterState state) => {
                    if (state != null)
                        await this.actionsToClientStream.OnNextAsync(new SyncCounterStateAction { CounterState = state });
                },
                (Exception e) => {
                    GetLogger().TrackException(e);
                });

        }

        public override async Task OnDeactivateAsync()
        {
            // clean up when grain goes away (nobody is looking at us anymore)
            this.storeSubscription.Dispose();
            this.storeSubscription = null;
            await this.actionStreamSubscription.UnsubscribeAsync();
            this.actionStreamSubscription = null;
            await base.OnDeactivateAsync();
        }

        public CounterGrain(ReduxTableStorage<CounterState> storage) : base(CounterState.Reducer, storage)
        {
        }

        public async Task IncrementCounter()
        {
            await this.Dispatch(new IncrementCounterAction());
            await this.WriteStateAsync();
        }

        public async Task DecrementCounter()
        {
            await this.Dispatch(new DecrementCounterAction());
            await this.WriteStateAsync();
        }

        public async Task StartCounterTimer()
        {
            if (this.timer != null)
                throw new Exception("Can't start: already started");

            await this.Dispatch(new StartCounterAction());
            await this.actionsToClientStream.OnNextAsync(new CounterStartedAction());

            this.timer = this.RegisterTimer(async (state) => {
                var action = new IncrementCounterAction();
                // This sends the action to the clients for processing there
                await this.actionsToClientStream.OnNextAsync(action);

                // This processes the action here on the server, and also sends the syncstate to make sure the outcome is the same
                // The order of events is important here
                await this.Dispatch(action);
                await this.WriteStateAsync();
            }, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));

            await this.actionsToClientStream.OnNextAsync(new CounterStartedAction());
        }

        public async Task StopCounterTimer()
        {
            if (this.timer == null)
                throw new Exception("Can't stop: not started");

            await this.Dispatch(new StopCounterAction());
            await this.actionsToClientStream.OnNextAsync(new CounterStoppedAction());
            this.timer.Dispose();
            this.timer = null;
        }

        public async Task Process(IAction action)
        {
            switch (action)
            {
                case IncrementCounterAction a:
                    await this.IncrementCounter();
                    break;
                case DecrementCounterAction a:
                    await this.DecrementCounter();
                    break;
                case StartCounterAction a:
                    await this.StartCounterTimer();
                    break;
                case StopCounterAction a:
                    await this.StopCounterTimer();
                    break;
                default:
                    throw new ArgumentException("Unknown Action received!");
            }
        }
    }
}
