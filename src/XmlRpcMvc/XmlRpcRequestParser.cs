using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Mvc;
using System.Xml;
using System.Linq;
using System.Reflection;
using XmlRpcMvc.Common;
using XmlRpcMvc.Extensions;

namespace XmlRpcMvc
{
    internal static class XmlRpcRequestParser
    {
        private static readonly Type _s_rpca = typeof(XmlRpcMethodAttribute);
        private static readonly Type _s_rpcs = typeof(IXmlRpcService);

        private static readonly Func<Type, bool> _s_isRpcService =
            type => (type.IsClass || type.IsInterface) &&
                    _s_rpcs.IsAssignableFrom(type);

        private static readonly Func<MethodInfo, XmlRpcMethodAttribute>
            _s_getRpcAttribute =
                method =>
                method
                    .GetCustomAttributes(_s_rpca, true)
                    .Cast<XmlRpcMethodAttribute>()
                    .FirstOrDefault();

        private static readonly
            Func<MethodInfo[], IEnumerable<XmlRpcMethodDescriptor>>
            _s_getXmlRpcMethods =
                methods =>
                from method in methods
                let attribute = _s_getRpcAttribute(method)
                where attribute != null
                select
                    new XmlRpcMethodDescriptor(
                    attribute.MethodName,
                    attribute.MethodDescription,
                    attribute.ResponseType,
                    method);

        private static readonly Func<Type, Type>
            _s_getImplementation =
                contract => AppDomain.CurrentDomain.GetAssemblies()
                                .Where(x => !x.GetName().Name.StartsWith("System.", StringComparison.OrdinalIgnoreCase))
                                .SelectMany(assembly => assembly.GetTypes())
                                .Where(type => !type.IsInterface)
                                .FirstOrDefault(type => contract.IsAssignableFrom(type));

        public static Dictionary<string, XmlRpcMethodDescriptor> GetMethods(
            Type[] services)
        {
            var types =
                services != null && services.Length > 0
                    ? services
                    : AppDomain.CurrentDomain
                          .GetAssemblies()
                          .SelectMany(x => x.GetTypes());

            return
                types
                    .Where(_s_isRpcService)
                    .Select(type => type.GetMethods())
                    .SelectMany(_s_getXmlRpcMethods)
                    .ToDictionary(desc => desc.Name, desc => desc);
        }

        public static XmlRpcMethodDescriptor GetRequestedMethod(
            XmlRpcRequest request, Type[] services)
        {
            var methods = GetMethods(services);

            XmlRpcMethodDescriptor descriptor;
            methods.TryGetValue(request.MethodName, out descriptor);
            return descriptor;
        }

        public static object ExecuteRequestedMethod(
            XmlRpcRequest request,
            XmlRpcMethodDescriptor methodDescriptor,
            ControllerBase controller)
        {
            var parameters = new List<object>();
            var method = methodDescriptor.MethodInfo;
            var requiredParameters = method.GetParameters();

            for (var i = 0; i < requiredParameters.Length; i++)
            {
                var parameter = requiredParameters[i];
                var type = parameter.ParameterType;

                if (type.IsPrimitive())
                {
                    parameters.Add(request.Parameters[i]);
                }
                else if (type.IsArray)
                {
                    var elementType = type.GetElementType();

                    var arrayParameters =
                        (object[])request.Parameters[i];

                    var listType = typeof(List<>);
                    var genericListType = listType.MakeGenericType(elementType);

                    var list = Activator.CreateInstance(genericListType);

                    foreach (Dictionary<string, object> values
                                in arrayParameters)
                    {
                        var element = Activator.CreateInstance(elementType);

                        foreach (var property
                                    in element.GetType().GetProperties())
                        {
                            var nameKey = property.GetSerializationName();

                            object value;
                            if (values.TryGetValue(nameKey, out value))
                            {
                                property.SetValue(element, value);
                            }
                        }

                        list.GetType()
                            .GetMethod("Add")
                            .Invoke(
                                list,
                                new[] { element });
                    }

                    parameters.Add(
                        list.GetType()
                            .GetMethod("ToArray")
                            .Invoke(list, null));
                }
                else
                {
                    var complexInstanceParameters =
                        (Dictionary<string, object>)request.Parameters[i];

                    var complexInstance =
                        Activator.CreateInstance(type);

                    foreach (var property in
                        complexInstance.GetType().GetProperties())
                    {
                        var nameKey = property.GetSerializationName();

                        object value;
                        if (complexInstanceParameters.TryGetValue(
                            nameKey,
                            out value))
                        {
                            property.SetValue(complexInstance, value);
                        }
                    }

                    parameters.Add(complexInstance);
                }
            }

            var instanceType = method.DeclaringType;

            if (method.DeclaringType != null &&
                method.DeclaringType.IsInterface)
            {
                instanceType = _s_getImplementation(method.DeclaringType);
            }

            var instance =
                controller.GetType() == instanceType
                    ? controller
                    : Activator.CreateInstance(instanceType);

            try
            {
                return method.Invoke(instance, parameters.ToArray());
            }
            catch (XmlRpcFaultException exception)
            {
                return exception;
            }
        }

        public static XmlRpcRequest GetRequestInformation(Stream xml)
        {
            var request = new XmlRpcRequest();

            var xmlDocument = new XmlDocument();
            xmlDocument.Load(xml);

            var methodName =
                xmlDocument.SelectSingleNode("methodCall/methodName");
            if (methodName != null)
            {
                request.MethodName = methodName.InnerText;
            }

            var parameters =
                xmlDocument.SelectNodes("methodCall/params/param/value/*");
            if (parameters != null)
            {
                request.Parameters = new List<object>();
                foreach (XmlNode node in parameters)
                {
                    request.Parameters.Add(GetMethodMember(request, node));
                }
            }

            return request;
        }

        private static object GetMethodMember(
            XmlRpcRequest request,
            XmlNode node)
        {
            switch (node.Name)
            {
                case "array":
                    return GetMethodArrayMember(request, node);

                case "struct":
                    return GetMethodStructMember(request, node);

                default:
                    return node.InnerText.ConvertTo(node.Name);
            }
        }

        private static Dictionary<string, object> GetMethodStructMember(
            XmlRpcRequest request,
            XmlNode node)
        {
            var xpath = node.GetXPath();
            var values = node.SelectNodes(string.Concat(xpath, "/member"));

            if (values != null)
            {
                var dictionary = new Dictionary<string, object>();

                foreach (XmlNode value in values)
                {
                    var memberNameNode = value["name"];
                    var memberValueNode = value["value"];

                    if (memberNameNode == null || memberValueNode == null)
                    {
                        continue;
                    }

                    var memberName = memberNameNode.InnerText;

                    dictionary.Add(
                        memberName,
                        GetMethodMember(
                            request,
                            memberValueNode.FirstChild));
                }

                return dictionary;
            }

            return null;
        }

        private static object GetMethodArrayMember(
            XmlRpcRequest request,
            XmlNode node)
        {
            var xpath = node.GetXPath();
            var values = node.SelectNodes(string.Concat(xpath, "/data/value"));

            var results = new List<object>();

            if (values != null)
            {
                foreach (XmlNode value in values)
                {
                    if (value.FirstChild.Name.Equals("struct"))
                    {
                        results.Add(GetMethodMember(request, value.FirstChild));
                    }
                    else
                    {
                        results.Add(value.InnerText.ConvertTo(value.FirstChild.Name));
                    }
                }

                //if (values
                //    .Cast<XmlNode>()
                //    .Any(x => x.FirstChild.Name.Equals("struct")))
                //{
                //    return values
                //        .Cast<XmlNode>()
                //        .Select(
                //            value =>
                //            GetMethodStructMember(
                //                request,
                //                value.FirstChild))
                //        .ToArray();
                //}

                //return values.Cast<XmlNode>()
                //    .Select(
                //        value =>
                //        (string) value.InnerText.ConvertTo(value.FirstChild.Name))
                //    .ToArray();


                // This works!!!
                //return values.Cast<XmlNode>()
                //    .Select(
                //        value =>
                //        (string)value.InnerText.ConvertTo(value.FirstChild.Name))
                //    .ToArray();

                return results.ToArray();
            }

            return null;
        }
    }
}