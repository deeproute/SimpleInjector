﻿#region Copyright (c) 2010 S. van Deursen
/* The Simple Injector is an easy-to-use Inversion of Control library for .NET
 * 
 * Copyright (C) 2010 S. van Deursen
 * 
 * To contact me, please visit my blog at http://www.cuttingedge.it/blogs/steven/ or mail to steven at 
 * cuttingedge.it.
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

using System;
using System.Diagnostics;
using System.Linq.Expressions;

namespace SimpleInjector.InstanceProducers
{
    internal sealed class FuncSingletonInstanceProducer<TService> : InstanceProducer where TService : class
    {
        private Func<TService> instanceCreator;

        internal FuncSingletonInstanceProducer(Func<TService> instanceCreator)
            : base(typeof(TService))
        {
            this.instanceCreator = instanceCreator;
        }

        internal override Func<object> BuildInstanceCreator()
        {
            var instance = this.instanceCreator();

            Func<object> singletonInstanceCreator = () => instance;

            return singletonInstanceCreator;
        }

        protected override Expression BuildExpressionCore()
        {
            var instance = this.GetInstanceFromCreator();

            return Expression.Constant(instance, this.ServiceType);
        }

        private TService GetInstanceFromCreator()
        {
            TService instance;

            try
            {
                instance = this.instanceCreator();
            }
            catch (ActivationException)
            {
                // This extra catch statement prevents ActivationExceptions from being wrapped in a new
                // ActivationException. This FuncSingletonInstanceProducer is used as wrapper around
                // TransientInstanceProducer instances that can throw ActivationException on their own.
                // Wrapping these again in a ActivationException would obfuscate the real error.
                throw;
            }
            catch (Exception ex)
            {
                throw new ActivationException(
                    StringResources.DelegateForTypeThrewAnException(typeof(TService), ex), ex);
            }

            if (instance == null)
            {
                throw new ActivationException(StringResources.DelegateForTypeReturnedNull(typeof(TService)));
            }

            return instance;
        }
    }
}