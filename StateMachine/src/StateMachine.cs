using System;
using System.Collections.Generic;
using System.Reflection;
using RamjetAnvil.Coroutine;

/*
 * Todo:
 * 
 * The owner's events should really just be references to delegates, I think. No need for multicast.
 * Special handling for parent->child and child-parent relationships?
 * 
 * Event propagation (damage dealing and handling with complex hierarchies is a good one)
 * Timed, cancelable transitions (camera motions are a good one)
 * Test more intricate parent->child relationships, callable states
 * 
 * 
 * 
 * Make running coroutines block state transitions. Unless canceling, or something.
 * 
 */

/*
 * Desired state machine declaration style:
 * 
 * {in-spawnpoint-menu: {transitions: [in-game]
 *                       child-transitions: [in-options-menu]}
 *  in-game:            {transitions: [in-spawnpoint-menu]
 *                       child-transitions: [in-options-menu]}
 *  in-options-menu     {transitions: [in-spawnpoint-menu in-game in-couse-editor]}
 *  in-course-editor    {child-transitions: [in-game, in-options-menu]
 *  in-spectator        {child-transitions: [in-options-menu]}
 * }         
 * 
 */

namespace RamjetAnvil.StateMachine {
    [AttributeUsage(AttributeTargets.Event)]
    public class StateEvent : Attribute {
        public string Name { get; private set; }

        public StateEvent(string name) {
            Name = name;
        }
    }

    public interface IStateMachine {
        CoroutineScheduler Scheduler { get; }

        void Transition(StateId stateId, params object[] args);
        void TransitionToParent();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <typeparam name="T">The type of the object that owns this StateMachine</typeparam>
    public class StateMachine<T> : IStateMachine {
        private readonly T _owner;
        private readonly CoroutineScheduler _scheduler;
        private readonly IDictionary<StateId, StateInstance> _states;
        private readonly IteratableStack<StateInstance> _stack;

        private readonly IDictionary<string, EventInfo> _ownerEvents;
        private bool _isTransitioning;

        public CoroutineScheduler Scheduler {
            get { return _scheduler; }
        }

        public StateMachine(T owner, CoroutineScheduler scheduler) {
            _owner = owner;
            _scheduler = scheduler;

            _states = new Dictionary<StateId, StateInstance>();
            _stack = new IteratableStack<StateInstance>();

            Type type = typeof (T);

            _ownerEvents = GetStateEvents(type);
            AssertStateMethodIntegrity(type, _ownerEvents);
        }

        public StateInstance AddState(StateId stateId, State state) {
            if (_states.ContainsKey(stateId)) {
                throw new ArgumentException(string.Format("StateId '{0}' is already registered.", stateId));
            }

            var instance = new StateInstance(stateId, state, GetImplementedStateMethods(state, _ownerEvents));
            _states.Add(stateId, instance);
            return instance;
        }

        public void Transition(StateId stateId, params object[] args) {
            if (_isTransitioning) {
                return;
            }

            StateInstance oldState = null;
            StateInstance newState = _states[stateId];

            if (_stack.Count > 0) {
                oldState = _stack.Peek();

                var isNormalTransition = oldState.Transitions.Contains(stateId);
                var isChildTransition = !isNormalTransition && oldState.ChildTransitions.Contains(stateId);

                if (!isNormalTransition && !isChildTransition) {
                    throw new Exception(string.Format(
                        "Transition from state '{0}' to state '{1}' is not registered, transition failed",
                        oldState.StateId,
                        stateId));
                }

                InvokeStateLifeCycleMethod(oldState.OnExit);

                if (isNormalTransition) {
                    _stack.Pop();
                }
            }

            _stack.Push(newState);
            SubscribeToStateMethods(oldState, newState);
            InvokeStateLifeCycleMethod(newState.OnEnter, args);
        }

        public void TransitionToParent() {
            if (_stack.Count <= 1) {
                throw new InvalidOperationException("Cannot transition to parent state, currently at top-level state");
            }

            var oldState = _stack.Pop();
            InvokeStateLifeCycleMethod(oldState.OnExit);
            SubscribeToStateMethods(oldState, _stack.Peek());
        }

        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static IDictionary<string, EventInfo> GetStateEvents(Type type) {
            var events = new Dictionary<string, EventInfo>();

            var attributeType = typeof (StateEvent);
            var allEvents = type.GetEvents(Flags);
            foreach (var e in allEvents) {
                var attributes = e.GetCustomAttributes(attributeType, false);
                if (attributes.Length > 0) {
                    var a = (StateEvent) attributes[0];
                    events.Add(a.Name, e);
                }
            }

            return events;
        }

        private static void AssertStateMethodIntegrity(Type type, IDictionary<string, EventInfo> ownerEvents) {
            foreach (var pair in ownerEvents) {
                var method = type.GetMethod(pair.Key, Flags);
                if (method == null) {
                    throw new StateMachineException(string.Format(
                        "Failed to find method matching event name '{0}' in '{1}'. Please implement method '{0}', and ensure it invokes '{2}'.",
                        pair.Key,
                        type,
                        pair.Value.Name));
                }
            }
        }

        private static IDictionary<string, Delegate> GetImplementedStateMethods(State state, IDictionary<string, EventInfo> ownerEvents) {
            var implementedMethods = new Dictionary<string, Delegate>();

            Type type = state.GetType();

            var stateMethods = type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var stateMethod in stateMethods) {
                foreach (var ownerEvent in ownerEvents) {
                    if (stateMethod.Name == ownerEvent.Key) {
                        var del = ReflectionUtils.ToDelegate(stateMethod, state);
                        implementedMethods.Add(ownerEvent.Key, del);
                    }
                }
            }

            return implementedMethods;
        }

        private void SubscribeToStateMethods(StateInstance oldState, StateInstance newState) {
            // Todo: is there an easier way to clear the list of subscribers?
            foreach (var pair in _ownerEvents) {
                // Unregister delegates of the old state
                if (oldState != null && oldState.StateDelegates.ContainsKey(pair.Key)) {
                    pair.Value.RemoveEventHandler(_owner, oldState.StateDelegates[pair.Key]);
                }
                // Register delegates of the new state
                if (newState.StateDelegates.ContainsKey(pair.Key)) {
                    pair.Value.AddEventHandler(_owner, newState.StateDelegates[pair.Key]);
                }
            }
        }

        /// <summary>
        /// Invokes OnEnter/OnExit function. Runs function as a coroutine if it is implemented as one.
        /// </summary>
        /// <param name="del"></param>
        /// <param name="args"></param>
        private void InvokeStateLifeCycleMethod(Delegate del, params object[] args) {
            if (del == null) {
                return;
            }

            try {
                if (del.Method.ReturnType == typeof(IEnumerator<WaitCommand>)) {
                    _scheduler.Start(WaitForTransition((IEnumerator<WaitCommand>)del.DynamicInvoke(args)));
                } else {
                    del.DynamicInvoke(args);
                }
            } catch (TargetParameterCountException e) {
                throw new ArgumentException(GetArgumentExceptionDetails((State)del.Target, del, args));
            }
        }

        /// <summary>
        ///  Performs the given transition coroutine, and block any state transitions while doing so
        /// </summary>
        /// <param name="transition"></param>
        /// <returns></returns>
        private IEnumerator<WaitCommand> WaitForTransition(IEnumerator<WaitCommand> transition) {
            
            _isTransitioning = true;
            yield return WaitCommand.WaitRoutine(transition);
            _isTransitioning = false;
        }

        private string GetArgumentExceptionDetails(State state, Delegate del, params object[] args) {
            var expectedArgs = del.Method.GetParameters();
            string expectedArgTypes = "";
            for (int i = 0; i < expectedArgs.Length; i++) {
                expectedArgTypes += expectedArgs[i].ParameterType.Name + (i < expectedArgs.Length - 1 ? ", " : "");
            }

            string receivedArgTypes = "";
            for (int i = 0; i < args.Length; i++) {
                receivedArgTypes += args[i].GetType().Name + (i < args.Length - 1 ? ", " : "");
            }
            return String.Format(
                "Wrong arguments for transition to state '{0}', expected: {1}; received: {2}",
                state.GetType(),
                expectedArgTypes,
                receivedArgTypes);
        }
    }

    public struct StateId {
        private readonly string _value;

        public StateId(string value) {
            _value = value;
        }

        public string Value {
            get { return _value; }
        }

        public bool Equals(StateId other) {
            return string.Equals(_value, other._value);
        }

        public override bool Equals(object obj) {
            if (ReferenceEquals(null, obj)) {
                return false;
            }
            return obj is StateId && Equals((StateId) obj);
        }

        public override int GetHashCode() {
            return (_value != null ? _value.GetHashCode() : 0);
        }

        public static bool operator ==(StateId left, StateId right) {
            return left.Equals(right);
        }

        public static bool operator !=(StateId left, StateId right) {
            return !left.Equals(right);
        }

        public override string ToString() {
            return _value;
        }
    }

    /// <summary>
    /// A state, plus metadata, which lives in the machine's stack
    /// </summary>
    /// Todo: only expose Permit interface to user, not the list of delegates etc.
    public class StateInstance {
        private readonly State _state;
        private readonly Delegate _onEnter;
        private readonly Delegate _onExit;
        private readonly IDictionary<string, Delegate> _stateDelegates;

        public Delegate OnEnter {
            get { return _onEnter; }
        }

        public Delegate OnExit {
            get { return _onExit; }
        }

        public StateInstance(StateId stateId, State state, IDictionary<string, Delegate> stateDelegates) {
            StateId = stateId;
            _state = state;
            _stateDelegates = stateDelegates;

            Transitions = new List<StateId>();
            ChildTransitions = new List<StateId>();

            _onEnter = GetDelegateByName(State, "OnEnter");
            _onExit = GetDelegateByName(State, "OnExit");
        }

        public StateId StateId { get; private set; }

        public State State {
            get { return _state; }
        }

        public IDictionary<string, Delegate> StateDelegates {
            get { return _stateDelegates; }
        }

        public IList<StateId> Transitions { get; private set; }
        public IList<StateId> ChildTransitions { get; private set; }

        public StateInstance Permit(StateId stateId) {
            Transitions.Add(stateId);
            return this;
        }

        public StateInstance PermitChild(StateId stateId) {
            ChildTransitions.Add(stateId);
            return this;
        }

        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static Delegate GetDelegateByName(State state, string name) {
            Type type = state.GetType();

            var method = type.GetMethod(name, Flags);
            if (method != null) {
                return ReflectionUtils.ToDelegate(method, state);
            }
            return null;
        }
    }

    public class State {
        protected IStateMachine Machine { get; private set; }

        public State(IStateMachine machine) {
            Machine = machine;
        }
    }

    public class StateMachineException : Exception {
        public StateMachineException(string message) : base(message) {}
    }
}