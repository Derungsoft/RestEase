﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Net.Http;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RestEase.Implementation
{
    /// <summary>
    /// Helper class used to generate interface implementations. Exposed for testing (and very adventurous people) only.
    /// </summary>
    public class ImplementationBuilder
    {
        private static readonly Regex pathParamMatch = new Regex(@"\{(.+?)\}");

        private static readonly string moduleBuilderName = "RestEaseAutoGeneratedModule";

        private static readonly MethodInfo requestVoidAsyncMethod = typeof(IRequester).GetMethod("RequestVoidAsync");
        private static readonly MethodInfo requestAsyncMethod = typeof(IRequester).GetMethod("RequestAsync");
        private static readonly MethodInfo requestWithResponseMessageAsyncMethod = typeof(IRequester).GetMethod("RequestWithResponseMessageAsync");
        private static readonly MethodInfo requestWithResponseAsyncMethod = typeof(IRequester).GetMethod("RequestWithResponseAsync");
        private static readonly MethodInfo requestRawAsyncMethod = typeof(IRequester).GetMethod("RequestRawAsync");
        private static readonly ConstructorInfo requestInfoCtor = typeof(RequestInfo).GetConstructor(new[] { typeof(HttpMethod), typeof(string) });
        private static readonly MethodInfo cancellationTokenSetter = typeof(RequestInfo).GetProperty("CancellationToken").SetMethod;
        private static readonly MethodInfo allowAnyStatusCodeSetter = typeof(RequestInfo).GetProperty("AllowAnyStatusCode").SetMethod;
        private static readonly MethodInfo addQueryParameterMethod = typeof(RequestInfo).GetMethod("AddQueryParameter");
        private static readonly MethodInfo addPathParameterMethod = typeof(RequestInfo).GetMethod("AddPathParameter");
        private static readonly MethodInfo setClassHeadersMethod = typeof(RequestInfo).GetProperty("ClassHeaders").SetMethod;
        private static readonly MethodInfo addMethodHeaderMethod = typeof(RequestInfo).GetMethod("AddMethodHeader");
        private static readonly MethodInfo addHeaderParameterMethod = typeof(RequestInfo).GetMethod("AddHeaderParameter");
        private static readonly MethodInfo setBodyParameterInfoMethod = typeof(RequestInfo).GetMethod("SetBodyParameterInfo");
        private static readonly ConstructorInfo listStringNCtor = typeof(List<string>).GetConstructor(new[] { typeof(int) });
        private static readonly MethodInfo listStringAdd = typeof(List<string>).GetMethod("Add");

        private static readonly Dictionary<HttpMethod, PropertyInfo> httpMethodProperties = new Dictionary<HttpMethod, PropertyInfo>()
        {
            { HttpMethod.Delete, typeof(HttpMethod).GetProperty("Delete") },
            { HttpMethod.Get, typeof(HttpMethod).GetProperty("Get") },
            { HttpMethod.Head, typeof(HttpMethod).GetProperty("Head") },
            { HttpMethod.Options, typeof(HttpMethod).GetProperty("Options") },
            { HttpMethod.Post, typeof(HttpMethod).GetProperty("Post") },
            { HttpMethod.Put, typeof(HttpMethod).GetProperty("Put") },
            { HttpMethod.Trace, typeof(HttpMethod).GetProperty("Trace") }
        };

        private readonly ModuleBuilder moduleBuilder;
        private readonly ConcurrentDictionary<Type, Func<IRequester, object>> creatorCache = new ConcurrentDictionary<Type, Func<IRequester, object>>();

        /// <summary>
        /// Initialises a new instance of the <see cref="ImplementationBuilder"/> class
        /// </summary>
        public ImplementationBuilder()
        {
            var assemblyName = new AssemblyName(RestClient.FactoryAssemblyName);
            var assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            var moduleBuilder = assemblyBuilder.DefineDynamicModule(moduleBuilderName);
            this.moduleBuilder = moduleBuilder;
        }

        /// <summary>
        /// Create an implementation of the given interface, using the given requester
        /// </summary>
        /// <typeparam name="T">Type of interface to implement</typeparam>
        /// <param name="requester">Requester to be used by the generated implementation</param>
        /// <returns>An implementation of the given interface</returns>
        public T CreateImplementation<T>(IRequester requester)
        {
            if (requester == null)
                throw new ArgumentNullException("requester");

            var creator = this.creatorCache.GetOrAdd(typeof(T), key =>
            {
                var implementationType = this.BuildImplementationImpl(key);
                return this.BuildCreator(implementationType);
            });

            T implementation = (T)creator(requester);

            return implementation;
        }

        private Func<IRequester, object> BuildCreator(Type implementationType)
        {
            var requesterParam = Expression.Parameter(typeof(IRequester));
            var ctor = Expression.New(implementationType.GetConstructor(new[] { typeof(IRequester) }), requesterParam);
            return Expression.Lambda<Func<IRequester, object>>(ctor, requesterParam).Compile();
        }

        private Type BuildImplementationImpl(Type interfaceType)
        {
            if (!interfaceType.IsInterface)
                throw new ArgumentException(String.Format("Type {0} is not an interface", interfaceType.Name));

            var typeBuilder = this.moduleBuilder.DefineType(String.Format("RestEase.AutoGenerated.{0}", interfaceType.FullName), TypeAttributes.Public);
            typeBuilder.AddInterfaceImplementation(interfaceType);

            var classHeaders = interfaceType.GetCustomAttributes<HeaderAttribute>().Select(x => x.Value).ToArray();
            var classAllowAnyStatusCodeAttribute = interfaceType.GetCustomAttribute<AllowAnyStatusCodeAttribute>();

            // Define a readonly field which holds a reference to the IRequester
            var requesterField = typeBuilder.DefineField("requester", typeof(IRequester), FieldAttributes.Private | FieldAttributes.InitOnly);

            this.AddInstanceCtor(typeBuilder, requesterField);

            // If there are any class headers, define a static readonly field which contains them
            // Also define a static constructor to initialise it
            FieldBuilder classHeadersField = null;
            if (classHeaders.Length > 0)
            {
                classHeadersField = typeBuilder.DefineField("classHeaders", typeof(List<string>), FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly);
                this.AddStaticCtor(typeBuilder, classHeaders, classHeadersField);
            }

            foreach (var methodInfo in interfaceType.GetMethods())
            {
                var requestAttribute = methodInfo.GetCustomAttribute<RequestAttribute>();
                if (requestAttribute == null)
                    throw new ImplementationCreationException(String.Format("Method {0} does not have a suitable attribute on it", methodInfo.Name));

                var allowAnyStatusCodeAttribute = methodInfo.GetCustomAttribute<AllowAnyStatusCodeAttribute>();

                var parameters = methodInfo.GetParameters();
                var parameterGrouping = new ParameterGrouping(parameters, methodInfo.Name);

                this.ValidatePathParams(requestAttribute.Path, parameterGrouping.PathParameters.Select(x => x.Attribute.Name ?? x.Parameter.Name), methodInfo.Name);

                var methodBuilder = typeBuilder.DefineMethod(methodInfo.Name, MethodAttributes.Public | MethodAttributes.Virtual, methodInfo.ReturnType, parameters.Select(x => x.ParameterType).ToArray());
                var methodIlGenerator = methodBuilder.GetILGenerator();

                this.AddRequestInfoCreation(methodIlGenerator, requesterField, requestAttribute);

                // If there's a cancellationtoken, add that
                if (parameterGrouping.CancellationToken.HasValue)
                {
                    methodIlGenerator.Emit(OpCodes.Dup);
                    methodIlGenerator.Emit(OpCodes.Ldarg, (short)parameterGrouping.CancellationToken.Value.Index);
                    methodIlGenerator.Emit(OpCodes.Callvirt, cancellationTokenSetter);
                }

                // If there are any class headers, add them
                if (classHeadersField != null)
                {
                    // requestInfo.ClassHeaders = classHeaders
                    methodIlGenerator.Emit(OpCodes.Dup);
                    methodIlGenerator.Emit(OpCodes.Ldsfld, classHeadersField);
                    methodIlGenerator.Emit(OpCodes.Callvirt, setClassHeadersMethod);
                }

                // If there are any method headers, add them
                var methodHeaders = methodInfo.GetCustomAttributes<HeaderAttribute>();
                foreach (var methodHeader in methodHeaders)
                {
                    this.AddMethodHeader(methodIlGenerator, methodHeader);
                }

                // If we want to allow any status code, set that
                var resolvedAllowAnyStatusAttribute = allowAnyStatusCodeAttribute ?? classAllowAnyStatusCodeAttribute;
                if (resolvedAllowAnyStatusAttribute != null && resolvedAllowAnyStatusAttribute.AllowAnyStatusCode)
                {
                    methodIlGenerator.Emit(OpCodes.Dup);
                    methodIlGenerator.Emit(OpCodes.Ldc_I4_1);
                    methodIlGenerator.Emit(OpCodes.Callvirt, allowAnyStatusCodeSetter);
                }

                this.AddParameters(methodIlGenerator, parameterGrouping);

                this.AddRequestMethodInvocation(methodIlGenerator, methodInfo);

                // Finally, return
                methodIlGenerator.Emit(OpCodes.Ret);

                typeBuilder.DefineMethodOverride(methodBuilder, methodInfo);
            }

            Type constructedType;
            try
            {
                constructedType = typeBuilder.CreateType();
            }
            catch (TypeLoadException e)
            {
                var msg = String.Format("Unable to create implementation for interface {0}. Ensure that the interface is public, or add [assembly: InternalsVisibleTo(RestClient.FactoryAssemblyName)] to your AssemblyInfo.cs", interfaceType.FullName);
                throw new ImplementationCreationException(msg, e);
            }

            return constructedType;
        }

        private void AddInstanceCtor(TypeBuilder typeBuilder, FieldBuilder requesterField)
        {
            // Add a constructor which takes the IRequester and assigns it to the field
            // public Name(IRequester requester)
            // {
            //     this.requester = requester;
            // }
            var ctorBuilder = typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, new[] { typeof(IRequester) });
            var ctorIlGenerator = ctorBuilder.GetILGenerator();
            // Load 'this' and the requester onto the stack
            ctorIlGenerator.Emit(OpCodes.Ldarg_0);
            ctorIlGenerator.Emit(OpCodes.Ldarg_1);
            // Store the requester into this.requester
            ctorIlGenerator.Emit(OpCodes.Stfld, requesterField);
            ctorIlGenerator.Emit(OpCodes.Ret);
        }

        private void AddStaticCtor(TypeBuilder typeBuilder, string[] classHeaders, FieldBuilder classHeadersField)
        {
            // static Name()
            // {
            //     classHeaders = new List<string>(n);
            //     classHeaders.Add("classHeaders");
            // }

            var staticCtorBuilder = typeBuilder.DefineConstructor(MethodAttributes.Static, CallingConventions.Standard, new Type[0]);
            var staticCtorIlGenerator = staticCtorBuilder.GetILGenerator();

            // Load the list size onto the stack
            // Stack: [list size]
            staticCtorIlGenerator.Emit(OpCodes.Ldc_I4, classHeaders.Length);
            // Ctor the list
            // Stack: [list]
            staticCtorIlGenerator.Emit(OpCodes.Newobj, listStringNCtor);
            // Load each class header into the list
            foreach (var classHeader in classHeaders)
            {
                staticCtorIlGenerator.Emit(OpCodes.Dup);
                staticCtorIlGenerator.Emit(OpCodes.Ldstr, classHeader);
                staticCtorIlGenerator.Emit(OpCodes.Callvirt, listStringAdd);
            }
            // Finally, store the list in its static field
            staticCtorIlGenerator.Emit(OpCodes.Stsfld, classHeadersField);
            staticCtorIlGenerator.Emit(OpCodes.Ret);
        }

        private void AddRequestInfoCreation(ILGenerator methodIlGenerator, FieldBuilder requesterField, RequestAttribute requestAttribute)
        {
            // Load 'this' onto the stack
            // Stack: [this]
            methodIlGenerator.Emit(OpCodes.Ldarg_0);
            // Load 'this.requester' onto the stack
            // Stack: [this.requester]
            methodIlGenerator.Emit(OpCodes.Ldfld, requesterField);

            // Start loading the ctor params for RequestInfo onto the stack
            // 1. HttpMethod
            // Stack: [this.requester, HttpMethod]
            methodIlGenerator.Emit(OpCodes.Call, httpMethodProperties[requestAttribute.Method].GetMethod);
            // 2. The Path
            // Stack: [this.requester, HttpMethod, path]
            methodIlGenerator.Emit(OpCodes.Ldstr, requestAttribute.Path);

            // Ctor the RequestInfo
            // Stack: [this.requester, requestInfo]
            methodIlGenerator.Emit(OpCodes.Newobj, requestInfoCtor);
        }

        private void AddParameters(ILGenerator methodIlGenerator, ParameterGrouping parameterGrouping)
        {
            // If there's a body, add it
            if (parameterGrouping.Body != null)
            {
                var body = parameterGrouping.Body.Value;
                this.AddBody(methodIlGenerator, body.Attribute.SerializationMethod, body.Parameter.ParameterType, (short)body.Index);
            }

            foreach (var queryParameter in parameterGrouping.QueryParameters)
            {
                var method = addQueryParameterMethod.MakeGenericMethod(queryParameter.Parameter.ParameterType);
                this.AddParam(methodIlGenerator, queryParameter.Attribute.Name ?? queryParameter.Parameter.Name, (short)queryParameter.Index, method);
            }

            foreach (var plainParameter in parameterGrouping.PlainParameters)
            {
                var method = addQueryParameterMethod.MakeGenericMethod(plainParameter.Parameter.ParameterType);
                this.AddParam(methodIlGenerator, plainParameter.Parameter.Name, (short)plainParameter.Index, method);
            }

            foreach (var pathParameter in parameterGrouping.PathParameters)
            {
                var method = addPathParameterMethod.MakeGenericMethod(pathParameter.Parameter.ParameterType);
                this.AddParam(methodIlGenerator, pathParameter.Attribute.Name ?? pathParameter.Parameter.Name, (short)pathParameter.Index, method);
            }

            foreach (var headerParameter in parameterGrouping.HeaderParameters)
            {
                this.AddParam(methodIlGenerator, headerParameter.Attribute.Value, (short)headerParameter.Index, addHeaderParameterMethod);
            }
        }

        private void AddRequestMethodInvocation(ILGenerator methodIlGenerator, MethodInfo methodInfo)
        { 
            // Call the appropriate RequestVoidAsync/RequestAsync method, depending on whether or not we have a return type
            if (methodInfo.ReturnType == typeof(Task))
            {
                // Stack: [Task]
                methodIlGenerator.Emit(OpCodes.Callvirt, requestVoidAsyncMethod);
            }
            else if (methodInfo.ReturnType.IsGenericType && methodInfo.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var typeOfT = methodInfo.ReturnType.GetGenericArguments()[0];
                // Now, is it a Task<HttpResponseMessage>, a Task<string>, a Task<Response<T>> or a Task<T>?
                if (typeOfT == typeof(HttpResponseMessage))
                {
                    // Stack: [Task<HttpResponseMessage>]
                    methodIlGenerator.Emit(OpCodes.Callvirt, requestWithResponseMessageAsyncMethod);
                }
                else if (typeOfT == typeof(string))
                {
                    // Stack: [Task<string>]
                    methodIlGenerator.Emit(OpCodes.Callvirt, requestRawAsyncMethod);
                }
                else if (typeOfT.IsGenericType && typeOfT.GetGenericTypeDefinition() == typeof(Response<>))
                {
                    // Stack: [Task<Response<T>>]
                    var typedRequestWithResponseAsyncMethod = requestWithResponseAsyncMethod.MakeGenericMethod(typeOfT.GetGenericArguments()[0]);
                    methodIlGenerator.Emit(OpCodes.Callvirt, typedRequestWithResponseAsyncMethod);
                }
                else
                {
                    // Stack: [Task<T>]
                    var typedRequestAsyncMethod = requestAsyncMethod.MakeGenericMethod(typeOfT);
                    methodIlGenerator.Emit(OpCodes.Callvirt, typedRequestAsyncMethod);
                }
            }
            else
            {
                throw new ImplementationCreationException(String.Format("Method {0} has a return type that is not Task<T> or Task", methodInfo.Name));
            }
        }

        private void AddBody(ILGenerator methodIlGenerator, BodySerializationMethod serializationMethod, Type parameterType, short parameterIndex)
        {
            // Equivalent C#:
            // requestInfo.SetBodyParameterInfo(serializationMethod, value)
            var typedMethod = setBodyParameterInfoMethod.MakeGenericMethod(parameterType);

            // Stack: [..., requestInfo, requestInfo]
            methodIlGenerator.Emit(OpCodes.Dup);
            // Stack: [..., requestInfo, requestInfo, serializationMethod]
            methodIlGenerator.Emit(OpCodes.Ldc_I4, (int)serializationMethod);
            // Stack: [..., requestInfo, requestInfo, serializationMethod, parameter]
            methodIlGenerator.Emit(OpCodes.Ldarg, parameterIndex);
            // Stack: [..., requestInfo]
            methodIlGenerator.Emit(OpCodes.Callvirt, typedMethod);
        }

        private void AddMethodHeader(ILGenerator methodIlGenerator, HeaderAttribute header)
        {
            // Equivalent C#:
            // requestInfo.AddMethodHeader("value");

            // Stack: [..., requestInfo, requestInfo]
            methodIlGenerator.Emit(OpCodes.Dup);
            // Stack: [..., requestInfo, requestInfo, "value"]
            methodIlGenerator.Emit(OpCodes.Ldstr, header.Value);
            // Stack: [..., requestInfo]
            methodIlGenerator.Emit(OpCodes.Callvirt, addMethodHeaderMethod);
        }

        private void AddParam(ILGenerator methodIlGenerator, string name, short parameterIndex, MethodInfo methodToCall)
        {
            // Equivalent C#:
            // requestInfo.methodToCall("name", value);
            // where 'value' is the parameter at index parameterIndex

            // Duplicate the requestInfo. This is because calling AddQueryParameter on it will pop it
            // Stack: [..., requestInfo, requestInfo]
            methodIlGenerator.Emit(OpCodes.Dup);
            // Load the name onto the stack
            // Stack: [..., requestInfo, requestInfo, name]
            methodIlGenerator.Emit(OpCodes.Ldstr, name);
            // Load the param onto the stack
            // Stack: [..., requestInfo, requestInfo, name, value]
            methodIlGenerator.Emit(OpCodes.Ldarg, parameterIndex);
            // Call AddPathParameter
            // Stack: [..., requestInfo]
            methodIlGenerator.Emit(OpCodes.Callvirt, methodToCall);
        }

        private void ValidatePathParams(string path, IEnumerable<string> pathParams, string methodName)
        {
            // Check that there are no duplicate param names in the attributes
            var duplicateKey = pathParams.GroupBy(x => x).FirstOrDefault(x => x.Count() > 1);
            if (duplicateKey != null)
                throw new ImplementationCreationException(String.Format("Found more than one path parameter for key {0}. Method: {1}", duplicateKey, methodName));

            // Check that each placeholder has a matching attribute, and vice versa
            var pathPartsSet = new HashSet<string>(pathParamMatch.Matches(path).Cast<Match>().Select(x => x.Groups[1].Value));
            pathPartsSet.SymmetricExceptWith(pathParams);
            var firstInvalid = pathPartsSet.FirstOrDefault();
            if (firstInvalid != null)
                throw new ImplementationCreationException(String.Format("Unable to find both a placeholder {{{0}}} and a [PathParam(\"{0}\")] for parameter {0}. Method: {1}", firstInvalid, methodName));
        }
    }
}
