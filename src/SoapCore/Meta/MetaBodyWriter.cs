using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;
using SoapCore.ServiceModel;

namespace SoapCore.Meta
{
	public class MetaBodyWriter : BodyWriter
	{
		private static int _namespaceCounter = 1;

		private readonly ServiceDescription _service;
		private readonly string _baseUrl;

		private readonly Queue<Type> _enumToBuild;
		private readonly Queue<Type> _complexTypeToBuild;
		private readonly Queue<Type> _arrayToBuild;

		private readonly HashSet<string> _builtEnumTypes;
		private readonly HashSet<string> _builtComplexTypes;
		private readonly HashSet<string> _buildArrayTypes;

		private readonly Dictionary<Type, Type> _wrappedTypes;

		private bool _buildDateTimeOffset;

		private MessageVersion _version;
		private bool _isSoap12 = true;

		public MetaBodyWriter(ServiceDescription service, string baseUrl, Binding binding) : base(isBuffered: true)
		{
			_service = service;
			_baseUrl = baseUrl;

			_enumToBuild = new Queue<Type>();
			_complexTypeToBuild = new Queue<Type>();
			_arrayToBuild = new Queue<Type>();
			_builtEnumTypes = new HashSet<string>();
			_builtComplexTypes = new HashSet<string>();
			_buildArrayTypes = new HashSet<string>();

			_wrappedTypes = new Dictionary<Type, Type>();

			if (binding != null)
			{
				BindingName = binding.Name;
				PortName = binding.Name;
				_version = binding.MessageVersion;
				_isSoap12 = _version == MessageVersion.Soap12WSAddressing10 || _version == MessageVersion.Soap12WSAddressingAugust2004;
			}
			else
			{
				BindingName = "BasicHttpBinding_" + _service.Contracts.First().Name;
				PortName = "BasicHttpBinding_" + _service.Contracts.First().Name;
			}
		}

		private string BindingName { get; }
		private string BindingType => _service.Contracts.First().Name;
		private string PortName { get; }

		private string TargetNameSpace => _service.Contracts.First().Namespace;

		protected override void OnWriteBodyContents(XmlDictionaryWriter writer)
		{
			AddTypes(writer);

			AddMessage(writer);

			AddPortType(writer);

			AddBinding(writer);

			AddService(writer);
		}

		private static string ResolveType(Type type)
		{
			string typeName = type.IsEnum ? type.GetEnumUnderlyingType().Name : type.Name;
			string resolvedType = null;

			switch (typeName)
			{
				case "Boolean":
					resolvedType = "xs:boolean";
					break;
				case "Byte":
					resolvedType = "xs:unsignedByte";
					break;
				case "Int16":
					resolvedType = "xs:short";
					break;
				case "Int32":
					resolvedType = "xs:int";
					break;
				case "Int64":
					resolvedType = "xs:long";
					break;
				case "SByte":
					resolvedType = "xs:byte";
					break;
				case "UInt16":
					resolvedType = "xs:unsignedShort";
					break;
				case "UInt32":
					resolvedType = "xs:unsignedInt";
					break;
				case "UInt64":
					resolvedType = "xs:unsignedLong";
					break;
				case "Decimal":
					resolvedType = "xs:decimal";
					break;
				case "Double":
					resolvedType = "xs:double";
					break;
				case "Single":
					resolvedType = "xs:float";
					break;
				case "DateTime":
					resolvedType = "xs:dateTime";
					break;
				case "Guid":
					resolvedType = "xs:string";
					break;
				case "Char":
					resolvedType = "xs:string";
					break;
				case "TimeSpan":
					resolvedType = "xs:duration";
					break;
			}

			if (string.IsNullOrEmpty(resolvedType))
			{
				throw new ArgumentException($".NET type {typeName} cannot be resolved into XML schema type");
			}

			return resolvedType;
		}

		private static Type GetGenericType(Type collectionType)
		{
			// Recursively look through the base class to find the Generic Type of the Enumerable
			var baseType = collectionType;
			var baseTypeInfo = collectionType.GetTypeInfo();
			while (!baseTypeInfo.IsGenericType && baseTypeInfo.BaseType != null)
			{
				baseType = baseTypeInfo.BaseType;
				baseTypeInfo = baseType.GetTypeInfo();
			}

			return baseType.GetTypeInfo().GetGenericArguments().DefaultIfEmpty(typeof(object)).FirstOrDefault();
		}

		private static bool IsWrappedMessageContractType(Type type)
		{
			var messageContractAttribute = type.GetCustomAttribute<MessageContractAttribute>();

			if (messageContractAttribute != null)
			{
				return messageContractAttribute.IsWrapped;
			}

			return false;
		}

		private static Type GetMessageContractBodyType(Type type)
		{
			var messageContractAttribute = type.GetCustomAttribute<MessageContractAttribute>();

			if (messageContractAttribute != null && !messageContractAttribute.IsWrapped)
			{
				var messageBodyMembers =
					type
						.GetPropertyOrFieldMembers()
						.Select(mi => new
						{
							Member = mi,
							MessageBodyMemberAttribute = mi.GetCustomAttribute<MessageBodyMemberAttribute>()
						})
						.Where(x => x.MessageBodyMemberAttribute != null)
						.OrderBy(x => x.MessageBodyMemberAttribute.Order)
						.ToList();

				return messageBodyMembers[0].Member.GetPropertyOrFieldType();
			}

			return type;
		}

		private void WriteParameters(XmlDictionaryWriter writer, SoapMethodParameterInfo[] parameterInfos, bool isMessageContract)
		{
			var hasWrittenSchema = false;

			foreach (var parameterInfo in parameterInfos)
			{
				var doWriteInlineType = true;

				if (isMessageContract)
				{
					doWriteInlineType = IsWrappedMessageContractType(parameterInfo.Parameter.ParameterType);
				}

				if (doWriteInlineType)
				{
					if (!hasWrittenSchema)
					{
						writer.WriteStartElement("xs", "complexType", Namespaces.XMLNS_XSD);
						writer.WriteStartElement("xs", "sequence", Namespaces.XMLNS_XSD);

						hasWrittenSchema = true;
					}

					var elementAttribute = parameterInfo.Parameter.GetCustomAttribute<XmlElementAttribute>();
					var parameterName = !string.IsNullOrEmpty(elementAttribute?.ElementName)
						? elementAttribute.ElementName
						: parameterInfo.Parameter.GetCustomAttribute<MessageParameterAttribute>()?.Name ?? parameterInfo.Parameter.Name;

					AddSchemaType(writer, parameterInfo.Parameter.ParameterType, parameterName, @namespace: elementAttribute?.Namespace);
				}
				else
				{
					var messageBodyType = GetMessageContractBodyType(parameterInfo.Parameter.ParameterType);

					writer.WriteAttributeString("type", "tns:" + messageBodyType.Name);
					_complexTypeToBuild.Enqueue(parameterInfo.Parameter.ParameterType);
				}
			}

			if (hasWrittenSchema)
			{
				writer.WriteEndElement(); // xs:sequence
				writer.WriteEndElement(); // xs:complexType
			}
		}

		private void AddTypes(XmlDictionaryWriter writer)
		{
			writer.WriteStartElement("wsdl", "types", Namespaces.WSDL_NS);
			writer.WriteStartElement("xs", "schema", Namespaces.XMLNS_XSD);
			writer.WriteXmlnsAttribute("xs", Namespaces.XMLNS_XSD);
			writer.WriteAttributeString("elementFormDefault", "qualified");
			writer.WriteAttributeString("targetNamespace", TargetNameSpace);

			writer.WriteStartElement("xs", "import", Namespaces.XMLNS_XSD);
			writer.WriteAttributeString("namespace", Namespaces.ARRAYS_NS);
			writer.WriteEndElement();

			writer.WriteStartElement("xs", "import", Namespaces.XMLNS_XSD);
			writer.WriteAttributeString("namespace", Namespaces.SYSTEM_NS);
			writer.WriteEndElement();

			foreach (var operation in _service.Operations)
			{
				// input parameters of operation
				writer.WriteStartElement("xs", "element", Namespaces.XMLNS_XSD);
				writer.WriteAttributeString("name", operation.Name);

				WriteParameters(writer, operation.InParameters, operation.IsMessageContractRequest);

				writer.WriteEndElement(); // xs:element

				// output parameter / return of operation
				writer.WriteStartElement("xs", "element", Namespaces.XMLNS_XSD);
				writer.WriteAttributeString("name", operation.Name + "Response");

				if (operation.DispatchMethod.ReturnType != typeof(void))
				{
					var returnType = operation.DispatchMethod.ReturnType;
					if (returnType.IsConstructedGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
					{
						returnType = returnType.GetGenericArguments().First();
					}

					var doWriteInlineType = true;

					if (operation.IsMessageContractResponse)
					{
						doWriteInlineType = IsWrappedMessageContractType(returnType);
					}

					if (doWriteInlineType)
					{
						var returnName = operation.DispatchMethod.ReturnParameter.GetCustomAttribute<MessageParameterAttribute>()?.Name ?? operation.Name + "Result";
						writer.WriteStartElement("xs", "complexType", Namespaces.XMLNS_XSD);
						writer.WriteStartElement("xs", "sequence", Namespaces.XMLNS_XSD);
						AddSchemaType(writer, returnType, returnName);
						writer.WriteEndElement();
						writer.WriteEndElement();
					}
					else
					{
						var type = GetMessageContractBodyType(returnType);

						writer.WriteAttributeString("type", "tns:" + type.Name);
						_complexTypeToBuild.Enqueue(returnType);
					}
				}

				WriteParameters(writer, operation.OutParameters, operation.IsMessageContractResponse);

				writer.WriteEndElement(); // xs:element
			}

			while (_complexTypeToBuild.Count > 0)
			{
				var toBuild = _complexTypeToBuild.Dequeue();

				var toBuildBodyType = GetMessageContractBodyType(toBuild);
				var isWrappedBodyType = IsWrappedMessageContractType(toBuild);

				var toBuildName = toBuildBodyType.IsArray ? "ArrayOf" + toBuildBodyType.Name.Replace("[]", string.Empty)
					: typeof(IEnumerable).IsAssignableFrom(toBuildBodyType) ? "ArrayOf" + GetGenericType(toBuildBodyType).Name
					: toBuildBodyType.Name;

				if (!_builtComplexTypes.Contains(toBuildName))
				{
					writer.WriteStartElement("xs", "complexType", Namespaces.XMLNS_XSD);
					if (toBuild.IsArray)
					{
						writer.WriteAttributeString("name", toBuildName);
					}
					else if (typeof(IEnumerable).IsAssignableFrom(toBuild))
					{
						writer.WriteAttributeString("name", toBuildName);
					}
					else
					{
						writer.WriteAttributeString("name", toBuildName);
					}

					writer.WriteStartElement("xs", "sequence", Namespaces.XMLNS_XSD);

					if (toBuild.IsArray)
					{
						AddSchemaType(writer, toBuild.GetElementType(), null, true);
					}
					else if (typeof(IEnumerable).IsAssignableFrom(toBuild))
					{
						AddSchemaType(writer, GetGenericType(toBuild), null, true);
					}
					else
					{
						if (!isWrappedBodyType)
						{
							foreach (var property in toBuildBodyType.GetProperties().Where(prop => !prop.CustomAttributes.Any(attr => attr.AttributeType == typeof(IgnoreDataMemberAttribute))))
							{
								AddSchemaType(writer, property.PropertyType, property.Name);
							}
						}
						else
						{
							foreach (var property in toBuild.GetProperties().Where(prop => !prop.CustomAttributes.Any(attr => attr.AttributeType == typeof(IgnoreDataMemberAttribute))))
							{
								AddSchemaType(writer, property.PropertyType, property.Name);
							}

							var messageBodyMemberFields = toBuild.GetFields()
								.Where(field => field.CustomAttributes.Any(attr => attr.AttributeType == typeof(MessageBodyMemberAttribute)))
								.OrderBy(field => field.GetCustomAttribute<MessageBodyMemberAttribute>().Order);

							foreach (var field in messageBodyMemberFields)
							{
								var messageBodyMember = field.GetCustomAttribute<MessageBodyMemberAttribute>();

								var fieldName = messageBodyMember.Name ?? field.Name;

								AddSchemaType(writer, field.FieldType, fieldName);
							}
						}
					}

					writer.WriteEndElement(); // xs:sequence
					writer.WriteEndElement(); // xs:complexType

					if (isWrappedBodyType)
					{
						writer.WriteStartElement("xs", "element", Namespaces.XMLNS_XSD);
						writer.WriteAttributeString("name", toBuildName);
						writer.WriteAttributeString("nillable", "true");
						writer.WriteAttributeString("type", "tns:" + toBuildName);
						writer.WriteEndElement(); // xs:element
					}

					_builtComplexTypes.Add(toBuildName);
				}
			}

			while (_enumToBuild.Count > 0)
			{
				Type toBuild = _enumToBuild.Dequeue();
				if (toBuild.IsByRef)
				{
					toBuild = toBuild.GetElementType();
				}

				if (!_builtEnumTypes.Contains(toBuild.Name))
				{
					writer.WriteStartElement("xs", "simpleType", Namespaces.XMLNS_XSD);
					writer.WriteAttributeString("name", toBuild.Name);
					writer.WriteStartElement("xs", "restriction", Namespaces.XMLNS_XSD);
					writer.WriteAttributeString("base", "xs:string");

					foreach (var value in Enum.GetValues(toBuild))
					{
						writer.WriteStartElement("xs", "enumeration", Namespaces.XMLNS_XSD);
						writer.WriteAttributeString("value", value.ToString());
						writer.WriteEndElement(); // xs:enumeration
					}

					writer.WriteEndElement(); // xs:restriction
					writer.WriteEndElement(); // xs:simpleType

					_builtEnumTypes.Add(toBuild.Name);
				}
			}

			writer.WriteEndElement(); // xs:schema

			while (_arrayToBuild.Count > 0)
			{
				var toBuild = _arrayToBuild.Dequeue();
				var toBuildName = toBuild.IsArray ? "ArrayOf" + toBuild.Name.Replace("[]", string.Empty)
					: typeof(IEnumerable).IsAssignableFrom(toBuild) ? "ArrayOf" + GetGenericType(toBuild).Name.ToLower()
					: toBuild.Name;

				if (!_buildArrayTypes.Contains(toBuildName))
				{
					writer.WriteStartElement("xs", "schema", Namespaces.XMLNS_XSD);
					writer.WriteXmlnsAttribute("xs", Namespaces.XMLNS_XSD);
					writer.WriteXmlnsAttribute("tns", Namespaces.ARRAYS_NS);
					writer.WriteAttributeString("elementFormDefault", "qualified");
					writer.WriteAttributeString("targetNamespace", Namespaces.ARRAYS_NS);

					writer.WriteStartElement("xs", "complexType", Namespaces.XMLNS_XSD);
					writer.WriteAttributeString("name", toBuildName);

					writer.WriteStartElement("xs", "sequence", Namespaces.XMLNS_XSD);
					AddSchemaType(writer, GetGenericType(toBuild), null, true);
					writer.WriteEndElement(); // xs:sequence

					writer.WriteEndElement(); // xs:complexType

					writer.WriteStartElement("xs", "element", Namespaces.XMLNS_XSD);
					writer.WriteAttributeString("name", toBuildName);
					writer.WriteAttributeString("nillable", "true");
					writer.WriteAttributeString("type", "tns:" + toBuildName);
					writer.WriteEndElement(); // xs:element

					writer.WriteEndElement(); // xs:schema

					_buildArrayTypes.Add(toBuildName);
				}
			}

			if (_buildDateTimeOffset)
			{
				writer.WriteStartElement("xs", "schema", Namespaces.XMLNS_XSD);
				writer.WriteXmlnsAttribute("xs", Namespaces.XMLNS_XSD);
				writer.WriteXmlnsAttribute("tns", Namespaces.SYSTEM_NS);
				writer.WriteAttributeString("elementFormDefault", "qualified");
				writer.WriteAttributeString("targetNamespace", Namespaces.SYSTEM_NS);

				writer.WriteStartElement("xs", "import", Namespaces.XMLNS_XSD);
				writer.WriteAttributeString("namespace", Namespaces.SERIALIZATION_NS);
				writer.WriteEndElement();

				writer.WriteStartElement("xs", "complexType", Namespaces.XMLNS_XSD);
				writer.WriteAttributeString("name", "DateTimeOffset");
				writer.WriteStartElement("xs", "annotation", Namespaces.XMLNS_XSD);
				writer.WriteStartElement("xs", "appinfo", Namespaces.XMLNS_XSD);

				writer.WriteElementString("IsValueType", Namespaces.SERIALIZATION_NS, "true");
				writer.WriteEndElement(); // xs:appinfo
				writer.WriteEndElement(); // xs:annotation

				writer.WriteStartElement("xs", "sequence", Namespaces.XMLNS_XSD);
				AddSchemaType(writer, typeof(DateTime), "DateTime", false);
				AddSchemaType(writer, typeof(short), "OffsetMinutes", false);
				writer.WriteEndElement(); // xs:sequence

				writer.WriteEndElement(); // xs:complexType

				writer.WriteStartElement("xs", "element", Namespaces.XMLNS_XSD);
				writer.WriteAttributeString("name", "DateTimeOffset");
				writer.WriteAttributeString("nillable", "true");
				writer.WriteAttributeString("type", "tns:DateTimeOffset");
				writer.WriteEndElement();

				writer.WriteEndElement(); // xs:schema
			}

			writer.WriteEndElement(); // wsdl:types
		}

		private void AddMessage(XmlDictionaryWriter writer)
		{
			foreach (var operation in _service.Operations)
			{
				// input
				var requestTypeName = operation.Name;

				if (operation.IsMessageContractRequest && operation.InParameters.Length > 0)
				{
					if (!IsWrappedMessageContractType(operation.InParameters[0].Parameter.ParameterType))
					{
						requestTypeName = GetMessageContractBodyType(operation.InParameters[0].Parameter.ParameterType).Name;
					}
				}

				writer.WriteStartElement("wsdl", "message", Namespaces.WSDL_NS);
				writer.WriteAttributeString("name", $"{BindingType}_{operation.Name}_InputMessage");
				writer.WriteStartElement("wsdl", "part", Namespaces.WSDL_NS);
				writer.WriteAttributeString("name", "parameters");
				writer.WriteAttributeString("element", "tns:" + requestTypeName);
				writer.WriteEndElement(); // wsdl:part
				writer.WriteEndElement(); // wsdl:message

				var responseTypeName = operation.Name + "Response";

				if (operation.DispatchMethod.ReturnType != typeof(void))
				{
					var returnType = operation.DispatchMethod.ReturnType;

					if (returnType.IsConstructedGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
					{
						returnType = returnType.GetGenericArguments().First();
					}

					if (!IsWrappedMessageContractType(returnType))
					{
						responseTypeName = GetMessageContractBodyType(returnType).Name;
					}
				}

				if (operation.IsMessageContractResponse && operation.OutParameters.Length > 0)
				{
					if (!IsWrappedMessageContractType(operation.OutParameters[0].Parameter.ParameterType))
					{
						responseTypeName = GetMessageContractBodyType(operation.OutParameters[0].Parameter.ParameterType).Name;
					}
				}

				// output
				writer.WriteStartElement("wsdl", "message", Namespaces.WSDL_NS);
				writer.WriteAttributeString("name", $"{BindingType}_{operation.Name}_OutputMessage");
				writer.WriteStartElement("wsdl", "part", Namespaces.WSDL_NS);
				writer.WriteAttributeString("name", "parameters");
				writer.WriteAttributeString("element", "tns:" + responseTypeName);
				writer.WriteEndElement(); // wsdl:part
				writer.WriteEndElement(); // wsdl:message
			}
		}

		private void AddPortType(XmlDictionaryWriter writer)
		{
			writer.WriteStartElement("wsdl", "portType", Namespaces.WSDL_NS);
			writer.WriteAttributeString("name", BindingType);
			foreach (var operation in _service.Operations)
			{
				writer.WriteStartElement("wsdl", "operation", Namespaces.WSDL_NS);
				writer.WriteAttributeString("name", operation.Name);
				writer.WriteStartElement("wsdl", "input", Namespaces.WSDL_NS);
				writer.WriteAttributeString("message", $"tns:{BindingType}_{operation.Name}_InputMessage");
				writer.WriteEndElement(); // wsdl:input
				writer.WriteStartElement("wsdl", "output", Namespaces.WSDL_NS);
				writer.WriteAttributeString("message", $"tns:{BindingType}_{operation.Name}_OutputMessage");
				writer.WriteEndElement(); // wsdl:output
				writer.WriteEndElement(); // wsdl:operation
			}

			writer.WriteEndElement(); // wsdl:portType
		}

		private void AddBinding(XmlDictionaryWriter writer)
		{
			writer.WriteStartElement("wsdl", "binding", Namespaces.WSDL_NS);
			writer.WriteAttributeString("name", BindingName);
			writer.WriteAttributeString("type", "tns:" + BindingType);

			var soap = _isSoap12 ? "soap12" : "soap";
			var soapNamespace = _isSoap12 ? Namespaces.SOAP12_NS : Namespaces.SOAP11_NS;
			writer.WriteStartElement(soap, "binding", soapNamespace);
			writer.WriteAttributeString("transport", Namespaces.TRANSPORT_SCHEMA);
			writer.WriteEndElement(); // soap:binding

			foreach (var operation in _service.Operations)
			{
				writer.WriteStartElement("wsdl", "operation", Namespaces.WSDL_NS);
				writer.WriteAttributeString("name", operation.Name);

				writer.WriteStartElement(soap, "operation", soapNamespace);
				writer.WriteAttributeString("soapAction", operation.SoapAction);
				writer.WriteAttributeString("style", "document");
				writer.WriteEndElement(); // soap:operation

				writer.WriteStartElement("wsdl", "input", Namespaces.WSDL_NS);
				writer.WriteStartElement(soap, "body", soapNamespace);
				writer.WriteAttributeString("use", "literal");
				writer.WriteEndElement(); // soap:body
				writer.WriteEndElement(); // wsdl:input

				writer.WriteStartElement("wsdl", "output", Namespaces.WSDL_NS);
				writer.WriteStartElement(soap, "body", soapNamespace);
				writer.WriteAttributeString("use", "literal");
				writer.WriteEndElement(); // soap:body
				writer.WriteEndElement(); // wsdl:output

				writer.WriteEndElement(); // wsdl:operation
			}

			writer.WriteEndElement(); // wsdl:binding
		}

		private void AddService(XmlDictionaryWriter writer)
		{
			var soap = _isSoap12 ? "soap12" : "soap";
			var soapNamespace = _isSoap12 ? Namespaces.SOAP12_NS : Namespaces.SOAP11_NS;

			writer.WriteStartElement("wsdl", "service", Namespaces.WSDL_NS);
			writer.WriteAttributeString("name", _service.ServiceType.Name);

			writer.WriteStartElement("wsdl", "port", Namespaces.WSDL_NS);
			writer.WriteAttributeString("name", PortName);
			writer.WriteAttributeString("binding", "tns:" + BindingName);

			writer.WriteStartElement(soap, "address", soapNamespace);

			writer.WriteAttributeString("location", _baseUrl);
			writer.WriteEndElement(); // soap:address

			writer.WriteEndElement(); // wsdl:port
		}

		private void AddSchemaType(XmlDictionaryWriter writer, Type type, string name, bool isArray = false, string @namespace = null)
		{
			var typeInfo = type.GetTypeInfo();
			if (typeInfo.IsByRef)
			{
				type = typeInfo.GetElementType();
			}

			if (writer.TryAddSchemaTypeFromXmlSchemaProviderAttribute(type, name, SoapSerializer.XmlSerializer))
			{
				return;
			}

			writer.WriteStartElement("xs", "element", Namespaces.XMLNS_XSD);

			// Check for null, since we may use empty NS
			if (@namespace != null)
			{
				writer.WriteAttributeString("targetNamespace", @namespace);
			}
			else if (typeInfo.IsEnum || (typeInfo.IsValueType && typeInfo.Namespace.StartsWith("System")))
			{
				string xsTypename;
				if (typeof(DateTimeOffset).IsAssignableFrom(type))
				{
					if (string.IsNullOrEmpty(name))
					{
						name = type.Name;
					}

					xsTypename = "nsdto:" + type.Name;
					writer.WriteXmlnsAttribute("nsdto", Namespaces.SYSTEM_NS);

					_buildDateTimeOffset = true;
				}
				else if (typeInfo.IsEnum)
				{
					xsTypename = "tns:" + type.Name;
					_enumToBuild.Enqueue(type);
				}
				else
				{
					var underlyingType = Nullable.GetUnderlyingType(type);
					if (underlyingType != null)
					{
						xsTypename = ResolveType(underlyingType);
						writer.WriteAttributeString("nillable", "true");
					}
					else
					{
						xsTypename = ResolveType(type);
					}
				}

				if (isArray)
				{
					writer.WriteAttributeString("minOccurs", "0");
					writer.WriteAttributeString("maxOccurs", "unbounded");
					writer.WriteAttributeString("nillable", "true");
				}
				else
				{
					writer.WriteAttributeString("minOccurs", "1");
					writer.WriteAttributeString("maxOccurs", "1");
				}

				if (string.IsNullOrEmpty(name))
				{
					name = xsTypename.Split(':')[1];
				}

				writer.WriteAttributeString("name", name);
				writer.WriteAttributeString("type", xsTypename);
			}
			else
			{
				writer.WriteAttributeString("minOccurs", "0");
				if (isArray)
				{
					writer.WriteAttributeString("maxOccurs", "unbounded");
					writer.WriteAttributeString("nillable", "true");
				}
				else
				{
					writer.WriteAttributeString("maxOccurs", "1");
				}

				if (type.Name == "String" || type.Name == "String&")
				{
					if (string.IsNullOrEmpty(name))
					{
						name = "string";
					}

					writer.WriteAttributeString("name", name);
					writer.WriteAttributeString("type", "xs:string");
				}
				else if (type.Name == "Byte[]")
				{
					if (string.IsNullOrEmpty(name))
					{
						name = "base64Binary";
					}

					writer.WriteAttributeString("name", name);
					writer.WriteAttributeString("type", "xs:base64Binary");
				}
				else if (type == typeof(Stream) || typeof(Stream).IsAssignableFrom(type))
				{
					name = "StreamBody";

					writer.WriteAttributeString("name", name);
					writer.WriteAttributeString("type", "xs:base64Binary");
				}
				else if (type.IsArray)
				{
					if (string.IsNullOrEmpty(name))
					{
						name = type.Name;
					}

					writer.WriteAttributeString("name", name);
					writer.WriteAttributeString("type", "tns:ArrayOf" + type.Name.Replace("[]", string.Empty));

					_complexTypeToBuild.Enqueue(type);
				}
				else if (typeof(IEnumerable).IsAssignableFrom(type))
				{
					if (GetGenericType(type).Name == "String")
					{
						if (string.IsNullOrEmpty(name))
						{
							name = type.Name;
						}

						var ns = $"q{_namespaceCounter++}";

						writer.WriteXmlnsAttribute(ns, Namespaces.ARRAYS_NS);
						writer.WriteAttributeString("name", name);
						writer.WriteAttributeString("nillable", "true");

						writer.WriteAttributeString("type", $"{ns}:ArrayOf{GetGenericType(type).Name.ToLower()}");

						_arrayToBuild.Enqueue(type);
					}
					else
					{
						if (string.IsNullOrEmpty(name))
						{
							name = type.Name;
						}

						writer.WriteAttributeString("name", name);

						if (!isArray)
						{
							writer.WriteAttributeString("nillable", "true");
						}

						writer.WriteAttributeString("type", "tns:ArrayOf" + GetGenericType(type).Name);

						_complexTypeToBuild.Enqueue(type);
					}
				}
				else
				{
					if (string.IsNullOrEmpty(name))
					{
						name = type.Name;
					}

					writer.WriteAttributeString("name", name);
					writer.WriteAttributeString("type", "tns:" + type.Name);

					_complexTypeToBuild.Enqueue(type);
				}
			}

			writer.WriteEndElement(); // xs:element
		}
	}
}
