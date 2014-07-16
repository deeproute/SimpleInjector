﻿#region Copyright Simple Injector Contributors
/* The Simple Injector is an easy-to-use Inversion of Control library for .NET
 * 
 * Copyright (c) 2013 Simple Injector Contributors
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
 * associated documentation files (the "Software"), to deal in the Software without restriction, including 
 * without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell 
 * copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the 
 * following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in all copies or substantial 
 * portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT 
 * LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO 
 * EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER 
 * IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE 
 * USE OR OTHER DEALINGS IN THE SOFTWARE.
*/
#endregion

namespace SimpleInjector.Extensions.Decorators
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using System.Threading;

    using SimpleInjector.Advanced;
    using SimpleInjector.Lifestyles;

    /// <summary>
    /// Hooks into the building process and adds a decorator if needed.
    /// </summary>
    internal abstract class DecoratorExpressionInterceptor
    {
        private readonly DecoratorExpressionInterceptorData data;

        protected DecoratorExpressionInterceptor(DecoratorExpressionInterceptorData data)
        {
            this.data = data;
        }

        protected Container Container
        {
            get { return this.data.Container; }
        }

        protected Lifestyle Lifestyle
        {
            get { return this.data.Lifestyle; }
        }

        // The decorator type definition (possibly open generic).
        protected Type DecoratorTypeDefinition
        {
            get { return this.data.DecoratorType; }
        }

        protected Predicate<DecoratorPredicateContext> Predicate
        {
            get { return this.data.Predicate; }
        }

        protected abstract Dictionary<InstanceProducer, ServiceTypeDecoratorInfo> ThreadStaticServiceTypePredicateCache
        {
            get;
        }

        // Store a ServiceTypeDecoratorInfo object per closed service type. We have a dictionary per
        // thread for thread-safety. We need a dictionary per thread, since the ExpressionBuilt event can
        // get raised by multiple threads at the same time (especially for types resolved using
        // unregistered type resolution) and using the same dictionary could lead to duplicate entries
        // in the ServiceTypeDecoratorInfo.AppliedDecorators list. Because the ExpressionBuilt event gets 
        // raised and all delegates registered to that event will get called on the same thread and before
        // an InstanceProducer stores the Expression, we can safely store this information in a 
        // thread-static field.
        // The key for retrieving the threadLocal value is supplied by the caller. This way both the 
        // DecoratorExpressionInterceptor and the ContainerUncontrolledServiceDecoratorInterceptor can have
        // their own dictionary. This is needed because they both use the same key, but store different
        // information.
        protected Dictionary<InstanceProducer, ServiceTypeDecoratorInfo> GetThreadStaticServiceTypePredicateCacheByKey(
            object key)
        {
            lock (key)
            {
                var threadLocal =
                    (ThreadLocal<Dictionary<InstanceProducer, ServiceTypeDecoratorInfo>>)this.Container.GetItem(key);

                if (threadLocal == null)
                {
                    threadLocal = new ThreadLocal<Dictionary<InstanceProducer, ServiceTypeDecoratorInfo>>();
                    this.Container.SetItem(key, threadLocal);
                }

                return threadLocal.Value ?? (threadLocal.Value = new Dictionary<InstanceProducer, ServiceTypeDecoratorInfo>());
            }
        }

        protected bool SatisfiesPredicate(DecoratorPredicateContext context)
        {
            return this.Predicate == null || this.Predicate(context);
        }

        protected ServiceTypeDecoratorInfo GetServiceTypeInfo(ExpressionBuiltEventArgs e)
        {
            return this.GetServiceTypeInfo(e.Expression, e.InstanceProducer, e.RegisteredServiceType, e.Lifestyle);
        }

        protected ServiceTypeDecoratorInfo GetServiceTypeInfo(Expression originalExpression,
            InstanceProducer registeredProducer, Type registeredServiceType, Lifestyle lifestyle)
        {
            // registeredProducer.ServiceType and registeredServiceType are different when called by 
            // container uncontrolled decorator. producer.ServiceType will be IEnumerable<T> and 
            // registeredServiceType will be T.
            Func<Type, InstanceProducer> producerBuilder = implementationType =>
            {
                // The InstanceProducer created here is used to do correct diagnostics. We can't return the
                // registeredProducer here, since the lifestyle of the original producer can change after
                // the ExpressionBuilt event has ran, which means that this would invalidate the diagnostic
                // results.
                return new InstanceProducer(registeredServiceType,
                    new ExpressionRegistration(originalExpression, implementationType, lifestyle, this.Container));
            };

            return this.GetServiceTypeInfo(originalExpression, registeredProducer, producerBuilder);
        }

        protected ServiceTypeDecoratorInfo GetServiceTypeInfo(Expression originalExpression,
            InstanceProducer registeredProducer, Func<Type, InstanceProducer> producerBuilder)
        {
            Type registeredServiceType = registeredProducer.ServiceType;

            var predicateCache = this.ThreadStaticServiceTypePredicateCache;

            if (!predicateCache.ContainsKey(registeredProducer))
            {
                Type implementationType =
                    ExtensionHelpers.DetermineImplementationType(originalExpression, registeredServiceType);

                var producer = producerBuilder(implementationType);

                predicateCache[registeredProducer] =
                    new ServiceTypeDecoratorInfo(registeredServiceType, implementationType, producer);
            }

            return predicateCache[registeredProducer];
        }

        protected Registration CreateRegistration(Type serviceType, ConstructorInfo decoratorConstructor,
            Expression decorateeExpression, InstanceProducer realProducer, ServiceTypeDecoratorInfo info)
        {
            ParameterInfo decorateeParameter = GetDecorateeParameter(serviceType, decoratorConstructor);

            decorateeExpression = GetExpressionForDecorateeDependencyParameterOrNull(
                decorateeParameter, serviceType, decorateeExpression);

            var currentProducer = info.GetCurrentInstanceProducer();

            if (IsDecorateeFactoryDependencyParameter(decorateeParameter, serviceType))
            {
                AddVerifierForDecorateeFactoryDependency(decorateeExpression, realProducer);

                currentProducer = this.CreateDecorateeFactoryProducer(decorateeParameter);
            }

            return this.Lifestyle.CreateRegistration(serviceType,
                decoratorConstructor.DeclaringType, this.Container,
                new OverriddenParameter(decorateeParameter, decorateeExpression, currentProducer));
        }

        protected static bool IsDecorateeParameter(ParameterInfo parameter, Type registeredServiceType)
        {
            return IsDecorateeDependencyParameter(parameter, registeredServiceType) ||
                IsDecorateeFactoryDependencyParameter(parameter, registeredServiceType);
        }

        protected static bool IsDecorateeFactoryDependencyParameter(ParameterInfo parameter, Type serviceType)
        {
            return parameter.ParameterType.IsGenericType &&
                parameter.ParameterType.GetGenericTypeDefinition() == typeof(Func<>) &&
                parameter.ParameterType == typeof(Func<>).MakeGenericType(serviceType);
        }

        protected DecoratorPredicateContext CreatePredicateContext(ExpressionBuiltEventArgs e)
        {
            return this.CreatePredicateContext(e.InstanceProducer, e.RegisteredServiceType, e.Expression, 
                e.Lifestyle);
        }

        protected DecoratorPredicateContext CreatePredicateContext(InstanceProducer registeredProducer,
            Type registeredServiceType, Expression expression, Lifestyle lifestyle)
        {
            var info = this.GetServiceTypeInfo(expression, registeredProducer, registeredServiceType, lifestyle);

            // NOTE: registeredServiceType can be different from registeredProducer.ServiceType.
            // This is the case for container uncontrolled collections where producer.ServiceType is the
            // IEnumerable<T> and registeredServiceType is T.
            return DecoratorPredicateContext.CreateFromInfo(registeredServiceType, expression, info);
        }

        protected static Expression GetExpressionForDecorateeDependencyParameterOrNull(
            ParameterInfo parameter, Type serviceType, Expression expression)
        {
            return
                BuildExpressionForDecorateeDependencyParameter(parameter, serviceType, expression) ??
                BuildExpressionForDecorateeFactoryDependencyParameter(parameter, serviceType, expression) ??
                null;
        }
        
        protected static ParameterInfo GetDecorateeParameter(Type serviceType, 
            ConstructorInfo decoratorConstructor)
        {
            return (
                from parameter in decoratorConstructor.GetParameters()
                where IsDecorateeParameter(parameter, serviceType)
                select parameter)
                .Single();
        }

        protected InstanceProducer CreateDecorateeFactoryProducer(ParameterInfo parameter)
        {
            // We create a dummy expression with a null value. Much easier than passing on the real delegate.
            // We won't miss it, since the created InstanceProducer is just a dummy for purposes of analysis.
            var dummyExpression = Expression.Constant(null, parameter.ParameterType);

            var registration = new ExpressionRegistration(dummyExpression, this.Container);

            return new InstanceProducer(parameter.ParameterType, registration);
        }
        
        private static void AddVerifierForDecorateeFactoryDependency(Expression decorateeExpression, 
            InstanceProducer producer)
        {
            // Func<T> dependencies for the decoratee must be explicitly added to the InstanceProducer as 
            // verifier. This allows those dependencies to be verified when calling Container.Verify().
            Action verifier = GetVerifierFromDecorateeExpression(decorateeExpression);

            producer.AddVerifier(verifier);
        }

        private static Action GetVerifierFromDecorateeExpression(Expression decorateeExpression)
        {
            Func<object> instanceCreator = (Func<object>)((ConstantExpression)decorateeExpression).Value;

            return () => instanceCreator();
        }
        
        // The constructor parameter in which the decorated instance should be injected.
        private static Expression BuildExpressionForDecorateeDependencyParameter(ParameterInfo parameter,
            Type serviceType, Expression expression)
        {
            if (IsDecorateeDependencyParameter(parameter, serviceType))
            {
                return expression;
            }

            return null;
        }

        private static bool IsDecorateeDependencyParameter(ParameterInfo parameter, Type registeredServiceType)
        {
            return parameter.ParameterType == registeredServiceType;
        }

        // The constructor parameter in which the factory for creating decorated instances should be injected.
        private static Expression BuildExpressionForDecorateeFactoryDependencyParameter(
            ParameterInfo parameter, Type serviceType, Expression expression)
        {
            if (IsDecorateeFactoryDependencyParameter(parameter, serviceType))
            {
                var instanceCreator =
                    Expression.Lambda(Expression.Convert(expression, serviceType)).Compile();

                return Expression.Constant(instanceCreator);
            }

            return null;
        }
    }
}