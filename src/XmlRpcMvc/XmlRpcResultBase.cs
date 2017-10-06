using Microsoft.AspNetCore.Mvc;
using System;
using System.Text;
using System.Xml;
using XmlRpcMvc.Extensions;

namespace XmlRpcMvc
{
    public class XmlRpcResult : ActionResult
    {
        private readonly Type[] _services;

        public XmlRpcResult(params Type[] services)
        {
            _services = services;
        }

        private bool _generateServiceOverview = true;
        public bool GenerateServiceOverview
        {
            get { return _generateServiceOverview; }
            set { _generateServiceOverview = value; }
        }

        public override void ExecuteResult(ActionContext context)
        {
            var request = context.HttpContext.Request;

            if (GenerateServiceOverview &&
                request.Method.Equals(
                    HttpVerbs.Get.ToString(),
                    StringComparison.OrdinalIgnoreCase))
            {
                new XmlRpcOverviewResult(GenerateServiceOverview, _services)
                    .ExecuteResult(context);

                return;
            }

            if (!request.Method.Equals(
                    HttpVerbs.Post.ToString(),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException();
            }

            var requestInfo =
                XmlRpcRequestParser.GetRequestInformation(
                    request.Body);

            if (string.IsNullOrWhiteSpace(requestInfo.MethodName))
            {
                throw new InvalidOperationException(
                    "XmlRpc call does not contain a method.");
            }

            var methodInfo =
                XmlRpcRequestParser.GetRequestedMethod(requestInfo, _services);

            if (methodInfo == null)
            {
                throw new InvalidOperationException(
                    string.Concat(
                        "There was no implementation of IXmlRpcService ",
                        "found, that containins a method decorated with ",
                        " the XmlRpcMethodAttribute value'",
                        requestInfo.MethodName,
                        "'."));
            }

            var result =
                XmlRpcRequestParser.ExecuteRequestedMethod(
                    requestInfo, methodInfo, context);

            var response = context.HttpContext.Response;
            response.ContentType = "text/xml";

            var settings =
                new XmlWriterSettings
                {
                    OmitXmlDeclaration = false,
                    Encoding = new UTF8Encoding(false), // Get rid of BOM
                    Indent = true,
                };

            using (var writer =
                XmlWriter.Create(response.Body, settings))
            {
                if (methodInfo.ResponseType == XmlRpcResponseType.Wrapped)
                {
                    WriteWrappedResponse(writer, result);
                    return;
                }
                WriteRawResponse(writer, result);
            }
        }

        private static void WriteRawResponse(
            XmlWriter output,
            dynamic result)
        {
            output.WriteStartDocument();
            {
                output.WriteStartElement("response");
                {
                    WriteObject(output, result);
                }
                output.WriteEndElement();
            }
            output.WriteEndDocument();
        }

        private static void WriteWrappedResponse(
            XmlWriter output,
            dynamic result)
        {
            output.WriteStartDocument();
            {
                output.WriteStartElement("methodResponse");
                {
                    output.WriteStartElement("params");
                    {
                        output.WriteStartElement("param");
                        {
                            output.WriteStartElement("value");
                            {
                                WriteObject(output, result);
                            }
                            output.WriteEndElement();
                        }
                        output.WriteEndElement();
                    }
                    output.WriteEndElement();
                }
                output.WriteEndElement();
            }
            output.WriteEndDocument();
        }

        private static void WriteObject(
            XmlWriter xmlWriter,
            dynamic result)
        {
            Type type = result.GetType();
            if (type.IsPrimitive())
            {
                xmlWriter.WrapOutgoingType((object)result);
            }
            else if (type.IsArray)
            {
                WriteArray(xmlWriter, result);
            }
            else if (!type.IsPrimitive && type.IsClass)
            {
                WriteClass(xmlWriter, type, result);
            }
        }

        private static void WriteClass(
            XmlWriter xmlWriter,
            Type type,
            object obj)
        {
            xmlWriter.WriteStartElement("struct");

            foreach (var property in type.GetProperties())
            {
                var value = property.GetValue(obj, null);
                if (value == null)
                    continue;

                xmlWriter.WriteStartElement("member");
                {
                    xmlWriter.WriteStartElement("name");
                    {
                        xmlWriter.WriteString(property.GetSerializationName());
                    }
                    xmlWriter.WriteEndElement();

                    xmlWriter.WriteStartElement("value");
                    {
                        WriteObject(xmlWriter, value);
                    }
                    xmlWriter.WriteEndElement();
                }
                xmlWriter.WriteEndElement();
            }

            // struct
            xmlWriter.WriteEndElement();
        }

        private static void WriteArray(XmlWriter xmlWriter, dynamic obj)
        {
            xmlWriter.WriteStartElement("array");
            {
                xmlWriter.WriteStartElement("data");
                {
                    foreach (var resultEntry in obj)
                    {
                        xmlWriter.WriteStartElement("value");
                        {
                            WriteObject(xmlWriter, resultEntry);
                        }
                        xmlWriter.WriteEndElement();
                    }
                }
                xmlWriter.WriteEndElement();
            }
            xmlWriter.WriteEndElement();
        }

    }
}