﻿using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Rebus.Activation;
using Rebus.Extensions;
using Rebus.Handlers;
using Rebus.Messages;
using Rebus.Sagas;
using Rebus.Transport;

namespace Rebus.Pipeline.Receive
{
    /// <summary>
    /// Incoming message step that gets relevant handlers for the message
    /// </summary>
    public class ActivateHandlersStep : IIncomingStep
    {
        readonly ConcurrentDictionary<Type, MethodInfo> _dispatchMethods = new ConcurrentDictionary<Type, MethodInfo>();
        readonly IHandlerActivator _handlerActivator;

        /// <summary>
        /// Constructs the step with the <see cref="IHandlerActivator"/> to use to get the handler instances
        /// </summary>
        public ActivateHandlersStep(IHandlerActivator handlerActivator)
        {
            _handlerActivator = handlerActivator;
        }

        public async Task Process(IncomingStepContext context, Func<Task> next)
        {
            var transactionContext = context.Load<ITransactionContext>();
            var message = context.Load<Message>();
            var messageId = message.Headers.GetValue(Headers.MessageId);
            var body = message.Body;
            var messageType = body.GetType();
            var methodToInvoke = _dispatchMethods
                .GetOrAdd(messageType, type => GetDispatchMethod(messageType));

            var handlerInvokers = await (Task<HandlerInvokers>)methodToInvoke.Invoke(this, new[] { messageId, body, transactionContext });

            context.Save(handlerInvokers);

            await next();
        }

        // ReSharper disable once UnusedMember.Local
        async Task<HandlerInvokers> GetHandlerInvokers<TMessage>(string messageId, TMessage message, ITransactionContext transactionContext)
        {
            var handlers = await _handlerActivator.GetHandlers(message, transactionContext);

            var listOfHandlerInvokers = handlers
                .Select(handler => new HandlerInvoker<TMessage>(messageId, () => handler.Handle(message), handler))
                .Cast<HandlerInvoker>()
                .ToList();

            return new HandlerInvokers(listOfHandlerInvokers);
        }

        MethodInfo GetDispatchMethod(Type messageType)
        {
            return GetType()
                .GetMethod("GetHandlerInvokers", BindingFlags.NonPublic | BindingFlags.Instance)
                .MakeGenericMethod(messageType);
        }
    }

    /// <summary>
    /// Wrapper of the handler that is ready to invoke
    /// </summary>
    public abstract class HandlerInvoker
    {
        /// <summary>
        /// Method to call in order to invoke this particular handler
        /// </summary>
        public abstract Task Invoke();
        
        /// <summary>
        /// Gets whether this invoker's handler is a saga
        /// </summary>
        public abstract bool HasSaga { get; }

        /// <summary>
        /// Gets this invoker's handler as a saga (throws if it's not a saga)
        /// </summary>
        public abstract Saga Saga { get; }

        /// <summary>
        /// Adds to the invoker a piece of saga data that has been determined to be relevant for the invocation
        /// </summary>
        public abstract void SetSagaData(ISagaData sagaData);
        
        /// <summary>
        /// Gets whether the contained saga handler can be initiated by messages of the given type
        /// </summary>
        public abstract bool CanBeInitiatedBy(Type messageType);

        /// <summary>
        /// Marks this handler as one to skip, i.e. calling this method will make the invoker ignore the call to <see cref="Invoke"/>
        /// </summary>
        public abstract void SkipInvocation();

        /// <summary>
        /// Gets the contained handler object (which is probably an implementation of <see cref="IHandleMessages"/>, but you should
        /// not depend on it!)
        /// </summary>
        public abstract object Handler { get; }
    }

    /// <summary>
    /// Derivation of the <see cref="HandlerInvoker"/> that has the message type
    /// </summary>
    public class HandlerInvoker<TMessage> : HandlerInvoker
    {
        readonly string _messageId;
        readonly Func<Task> _action;
        readonly object _handler;
        ISagaData _sagaData;
        bool _invokeHandler = true;

        /// <summary>
        /// Constructs the invoker
        /// </summary>
        public HandlerInvoker(string messageId, Func<Task> action, object handler)
        {
            _messageId = messageId;
            _action = action;
            _handler = handler;
        }

        public override object Handler
        {
            get { return _handler; }
        }

        public override bool HasSaga
        {
            get { return _handler is Saga; }
        }

        public override Saga Saga
        {
            get
            {
                if (!HasSaga) throw new InvalidOperationException(string.Format("Attempted to get {0} as saga, it's not a saga!", _handler));
                
                return (Saga)_handler;
            }
        }

        public override async Task Invoke()
        {
            if (!_invokeHandler) return;

            await _action();
        }

        public override void SetSagaData(ISagaData sagaData)
        {
            if (!HasSaga) throw new InvalidOperationException(string.Format("Attempted to set {0} as saga data on handler {1}, but the handler is not a saga!",
                sagaData, _handler));

            var dataProperty = _handler.GetType().GetProperty("Data");

            if (dataProperty == null)
            {
                throw new ApplicationException(string.Format("Could not find the 'Data' property on {0}...", _handler));
            }

            dataProperty.SetValue(_handler, sagaData);

            _sagaData = sagaData;
        }

        public override bool CanBeInitiatedBy(Type messageType)
        {
            var handlerTypeToLookFor = typeof(IAmInitiatedBy<>).MakeGenericType(messageType);

            return _handler.GetType().GetInterfaces().Contains(handlerTypeToLookFor);
        }

        public override void SkipInvocation()
        {
            _invokeHandler = false;
        }
    }
}