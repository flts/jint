﻿using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Linq.Expressions;
using Jint.Native;
using System.Collections.Generic;
using System.Reflection;

namespace Jint.Runtime.Interop
{
    public class DefaultTypeConverter : ITypeConverter
    {
        private readonly Engine _engine;
        private static readonly Dictionary<string, bool> _knownConversions = new Dictionary<string, bool>();
        private static readonly object _lockObject = new object();

        private static MethodInfo convertChangeType = typeof(System.Convert).GetMethod("ChangeType", new Type[] { typeof(object), typeof(Type), typeof(IFormatProvider) } );
        private static MethodInfo jsValueFromObject = typeof(JsValue).GetMethod("FromObject");
        private static MethodInfo jsValueToObject = typeof(JsValue).GetMethod("ToObject");

        public DefaultTypeConverter(Engine engine)
        {
            _engine = engine;
        }

        public virtual object Convert(object value, Type type, IFormatProvider formatProvider)
        {
            if (value == null)
            {
                if (TypeConverter.TypeIsNullable(type))
                {
                    return null;
                }

                throw new NotSupportedException(string.Format("Unable to convert null to '{0}'", type.FullName));
            }

            // don't try to convert if value is derived from type
            if (type.IsInstanceOfType(value))
            {
                return value;
            }

            if (type.IsEnum)
            {
                var integer = System.Convert.ChangeType(value, typeof(int), formatProvider);
                if (integer == null)
                {
                    throw new ArgumentOutOfRangeException();
                }

                return Enum.ToObject(type, integer);
            }

            var valueType = value.GetType();
            // is the javascript value an ICallable instance ?
            if (valueType == typeof(Func<JsValue, JsValue[], JsValue>))
            {
                var function = (Func<JsValue, JsValue[], JsValue>)value;

                if (type.IsGenericType)
                {
                    var genericType = type.GetGenericTypeDefinition();

                    // create the requested Delegate
                    if (genericType.Name.StartsWith("Action"))
                    {
                        var genericArguments = type.GetGenericArguments();

                        var @params = new ParameterExpression[genericArguments.Count()];
                        for (var i = 0; i < @params.Count(); i++)
                        {
                            @params[i] = Expression.Parameter(genericArguments[i], genericArguments[i].Name + i);
                        }

                        var tmpVars = new System.Linq.Expressions.Expression[@params.Length]; // .NET 3.5 compatibility: added full namespace because of namespace conflicts
                        for (var i = 0; i < @params.Count(); i++)
                        {
                            var param = @params[i];
                            if (param.Type.IsValueType)
                            {
                                var boxing = Expression.Convert(param, typeof(object));
                                tmpVars[i] = Expression.Call(null, jsValueFromObject, Expression.Constant(_engine, typeof(Engine)), boxing);
                            }
                            else
                            {
                                tmpVars[i] = Expression.Call(null, jsValueFromObject, Expression.Constant(_engine, typeof(Engine)), param);
                            }
                        }
                        var @vars = Expression.NewArrayInit(typeof(JsValue), tmpVars);

                        var callExpresion = Expression.Block(Expression.Call(
                                                Expression.Call(Expression.Constant(function.Target),
                                                    function.Method,
                                                    Expression.Constant(JsValue.Undefined, typeof(JsValue)),
                                                    @vars),
                                                jsValueToObject), Expression.Empty());

                        return Expression.Lambda(Expression.GetActionType(genericArguments), callExpresion, @params);  // .NET 3.5 compatibility: added type parameter AND removed ReadOnlyCollection<> maybe also push change to MASTER

                    }
                    else if (genericType.Name.StartsWith("Func"))
                    {
                        var genericArguments = type.GetGenericArguments();
                        var returnType = genericArguments.Last();

                        var @params = new ParameterExpression[genericArguments.Count() - 1];
                        for (var i = 0; i < @params.Count(); i++)
                        {
                            @params[i] = Expression.Parameter(genericArguments[i], genericArguments[i].Name + i);
                        }

                        var @vars = 
                            Expression.NewArrayInit(typeof(JsValue), 
                                @params.Select(p => {
                                    var boxingExpression = Expression.Convert(p, typeof(object));
                                    return Expression.Call(null, jsValueFromObject, Expression.Constant(_engine, typeof(Engine)), boxingExpression);
                                }).ToArray() /* .NET 3.5 compatibility: added .ToArray() */
                            );

                        // the final result's type needs to be changed before casting,
                        // for instance when a function returns a number (double) but C# expects an integer

                        var callExpresion = Expression.Convert(
                                                Expression.Call(null,
                                                    convertChangeType,
                                                    Expression.Call(
                                                            Expression.Call(Expression.Constant(function.Target),
                                                                    function.Method,
                                                                    Expression.Constant(JsValue.Undefined, typeof(JsValue)),
                                                                    @vars),
                                                            jsValueToObject),
                                                        Expression.Constant(returnType, typeof(Type)),
                                                        Expression.Constant(System.Globalization.CultureInfo.InvariantCulture, typeof(IFormatProvider))
                                                        ),                            
                                                    returnType);

                        return Expression.Lambda(callExpresion, @params);  // .NET 3.5 compatibility: removed ReadOnlyCollection<> maybe also push change to MASTER
                    }
                }
                else
                {
                    if (type == typeof(Action))
                    {
                        return (Action)(() => function(JsValue.Undefined, new JsValue[0]));
                    }
                    else if (type.IsSubclassOf(typeof(System.MulticastDelegate)))
                    {
                        var method = type.GetMethod("Invoke");
                        var arguments = method.GetParameters();

                        var @params = new ParameterExpression[arguments.Count()];
                        for (var i = 0; i < @params.Count(); i++)
                        {
                            @params[i] = Expression.Parameter(typeof(object), arguments[i].Name);
                        }
                        var @vars = Expression.NewArrayInit(typeof(JsValue), @params.Select(p => Expression.Call(null, jsValueFromObject, Expression.Constant(_engine, typeof(Engine)), p)).ToArray());  // .NET 3.5 compatibility: added .ToArray()

                        var callExpression = Expression.Block(
                                                Expression.Call(
                                                    Expression.Call(Expression.Constant(function.Target),
                                                        function.Method,
                                                        Expression.Constant(JsValue.Undefined, typeof(JsValue)),
                                                        @vars),
                                                    jsValueToObject),
                                                Expression.Empty());

                        var dynamicExpression = Expression.Invoke(Expression.Lambda(callExpression, @params), @params);

                        return Expression.Lambda(type, dynamicExpression, @params); // .NET 3.5 compatibility: removed ReadOnlyCollection<> maybe also push change to MASTER
                    }
                }
            }

            if (type.IsArray)
            {
                var source = value as object[];
                if (source == null)
                    throw new ArgumentException(String.Format("Value of object[] type is expected, but actual type is {0}.", value.GetType()));

                var targetElementType = type.GetElementType();
                var itemsConverted = source.Select(o => Convert(o, targetElementType, formatProvider)).ToArray();
                var result = Array.CreateInstance(targetElementType, source.Length);
                itemsConverted.CopyTo(result, 0);
                return result;
            }

            return System.Convert.ChangeType(value, type, formatProvider);
        }

        public virtual bool TryConvert(object value, Type type, IFormatProvider formatProvider, out object converted)
        {
            bool canConvert;
            var key = value == null ? String.Format("Null->{0}", type) : String.Format("{0}->{1}", value.GetType(), type);

            if (!_knownConversions.TryGetValue(key, out canConvert))
            {
                lock (_lockObject)
                {
                    if (!_knownConversions.TryGetValue(key, out canConvert))
                    {
                        try
                        {
                            converted = Convert(value, type, formatProvider);
                            _knownConversions.Add(key, true);
                            return true;
                        }
                        catch
                        {
                            converted = null;
                            _knownConversions.Add(key, false);
                            return false;
                        }
                    }
                }
            }

            if (canConvert)
            {
                converted = Convert(value, type, formatProvider);
                return true;
            }

            converted = null;
            return false;
        }


        /// <summary>
        /// This custom Expression class extends the base class in .NET 3.5 so ExpressionEmpty and ExpressionBlock are simulated for no code change in DefaultTypeConverter for better interoptability with the .NET 4.X version.
        /// </summary>
        protected class Expression : System.Linq.Expressions.Expression
        {
            protected Expression(ExpressionType expressionType, Type type) : base(expressionType, type) { }
            
            public static System.Linq.Expressions.Expression Empty()
            {
                return null;
            }

            public static System.Linq.Expressions.MethodCallExpression Block(System.Linq.Expressions.Expression expression, System.Linq.Expressions.Expression parameter)
            {
                return expression as MethodCallExpression;
            }
        }
    }
}
