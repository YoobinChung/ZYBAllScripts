// Toony Colors Pro 2
// (c) 2014-2026 Jean Moreno

#define WRITE_UNCOMPRESSED_SERIALIZED_DATA

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using UnityEngine.Scripting;
using ToonyColorsPro.Utilities;
using ToonyColorsPro.ShaderGenerator.CodeInjection;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

// -----------------------------------------------------------------------------
// Merged from Serialization.cs
// -----------------------------------------------------------------------------

// Reflection-based serialization system: serialize simple value types, and specific classes (either those with the SerializeAs attribute, or special ones like Vector2, Vector3, ...)
// Used to serialize data and add it as a comment in generated shaders

namespace ToonyColorsPro
{
	namespace ShaderGenerator
	{
		public class Serialization
		{
			/// <summary>
			/// Declare a class or field as serializable, and set its serialized short name
			/// </summary>
			[AttributeUsage(AttributeTargets.Field | AttributeTargets.Class | AttributeTargets.Property)]
			public class SerializeAsAttribute : Attribute
			{
				/// <summary>
				/// The short name to serialize that object, to reduce length of the serialized string.
				/// </summary>
				public string serializedName;

				/// <summary>
				/// Name of the field or property that will determine if the object can be serialized.
				/// Originally used to check if a Shader Property has been manually modified.
				/// </summary>
				public string conditionalField;

				public SerializeAsAttribute(string name, string conditionalField = null)
				{
					this.serializedName = name;
					this.conditionalField = conditionalField;
				}
			}

			/// <summary>
			/// Force serialization, regardless of the "conditionalField" attribute value
			/// </summary>
			[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
			public class ForceSerializationAttribute : Attribute { }

			/// <summary>
			/// Declare a method as a callback to deserialize an object manually
			/// </summary>
			[AttributeUsage(AttributeTargets.Method)]
			public class CustomDeserializeCallbackAttribute : Attribute
			{
				public CustomDeserializeCallbackAttribute() { }
			}

			/// <summary>
			/// Declare a method as a callback after an object has been deserialized
			/// </summary>
			[AttributeUsage(AttributeTargets.Method)]
			public class OnDeserializeCallbackAttribute : Attribute
			{
				public OnDeserializeCallbackAttribute() { }
			}

			//Will serialize an object as "type(field:value,field2:value,field3:value...)" provided that they have fields with the [SerializeAs] attribute
			public static string Serialize(object obj, FieldInfo objFieldInfo = null)
			{
				var output = "";

				//fetch class SerializedAs attribute
				var classAttributes = obj.GetType().GetCustomAttributes(typeof(SerializeAsAttribute), false);
				if (classAttributes != null && classAttributes.Length == 1)
				{
					var serializedAsAttribute = (classAttributes[0] as SerializeAsAttribute);

					//class has a conditional serialization?
					var conditionalFieldName = serializedAsAttribute.conditionalField;
					if (!string.IsNullOrEmpty(conditionalFieldName))
					{
						var forceSerialization = objFieldInfo != null && ((Attribute[])objFieldInfo.GetCustomAttributes(typeof(ForceSerializationAttribute))).Length == 1;
						if (!forceSerialization)
						{
							//try field
							var conditionalField = obj.GetType().GetField(conditionalFieldName);
							if (conditionalField != null)
							{
								if (!(bool) conditionalField.GetValue(obj))
								{
									return null;
								}
							}
							else
							{
								//try property
								var conditionalProperty = obj.GetType().GetProperty(conditionalFieldName);
								if (conditionalProperty != null)
								{
									if (!(bool) conditionalProperty.GetValue(obj, null))
									{
										return null;
									}
								}
								else
								{
									Debug.LogError(string.Format("Conditional field or property '{0}' doesn't exist for type '{1}'", conditionalFieldName, obj.GetType()));
								}
							}
						}
					}

					var name = serializedAsAttribute.serializedName;
					output = name + "(";
				}

				// properties with [SerializeAs] attribute
				// note: only used for unityVersion currently; see Config.cs
				var properties = new List<PropertyInfo>(obj.GetType().GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
				foreach (var prop in properties)
				{
					var attributes = prop.GetCustomAttributes(typeof(SerializeAsAttribute), true);
					if (attributes != null && attributes.Length == 1)
					{
						var name = (attributes[0] as SerializeAsAttribute).serializedName;
						output += string.Format("{0}:\"{1}\";", name, prop.GetValue(obj, null));
					}
				}

				//get all fields, and look for [SerializeAs] attribute
				var fields = new List<FieldInfo>(obj.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
				foreach (var field in fields)
				{
					var attributes = field.GetCustomAttributes(typeof(SerializeAsAttribute), true);
					if (attributes != null && attributes.Length == 1)
					{
						var name = (attributes[0] as SerializeAsAttribute).serializedName;

						//returns the value of an object as a string
						Func<object, string> GetStringValue = null;
						GetStringValue = @object =>
						{
							if (@object == null)
							{
								// Debug.LogError("Serialization error!\nTrying to get the string value of a null object.");
								return "__NULL__";
							}

							var type = @object.GetType();

							//object types
							if (!type.IsValueType && type != typeof(string))
							{
								//list
								if (@object is IList)
								{
									var list = @object as IList;
									var values = "list[";
									foreach (var value in list)
										values += GetStringValue(value) + ",";
									return values.TrimEnd(',') + "]";
								}
								//dictionary
								if (@object is IDictionary)
								{
									var dict = @object as IDictionary;
									var kvp = "dict[";
									foreach (DictionaryEntry entry in dict)
										kvp += entry.Key + "=" + GetStringValue(entry.Value) + ",";
									return kvp.TrimEnd(',') + "]";
								}
								//else try to serialize with this serializer
								var refAttributes = field.GetCustomAttributes(typeof(SerializeAsAttribute), true);
								if (refAttributes != null && refAttributes.Length == 1)
								{
									//serializable
									return Serialize(@object, field);
								}

								return null;
							}
							//string: enclose in quotes to prevent parsing errors (e.g. with parenthesis)
							if (type == typeof(string))
							{
								return string.Format("\"{0}\"", @object);
							}
							
							// unity vectors: prevent printing values with commas
							if (type == typeof(Vector2))
							{
								var v2 = (Vector2) @object;
								return string.Format(CultureInfo.InvariantCulture, "({0}, {1})", v2.x, v2.y);
							}
							if (type == typeof(Vector3))
							{
								var v3 = (Vector3) @object;
								return string.Format(CultureInfo.InvariantCulture, "({0}, {1}, {2})", v3.x, v3.y, v3.z);
							}
							if (type == typeof(Vector4))
							{
								var v4 = (Vector4) @object;
								return string.Format(CultureInfo.InvariantCulture, "({0}, {1}, {2}, {3})", v4.x, v4.y, v4.z, v4.w);
							}
							if (type == typeof(Color))
							{
								var c = (Color) @object;
								return string.Format(CultureInfo.InvariantCulture, "RGBA({0}, {1}, {2}, {3})", c.r, c.g, c.b, c.a);
								// return string.Format(CultureInfo.InvariantCulture, "{0}", c);
							}
							
							//value type: just return the toString version
							return string.Format(CultureInfo.InvariantCulture, "{0}", @object);
						};

						var val = GetStringValue(field.GetValue(obj));
						if (val == null) 
						{
							Debug.LogError(string.Format("Can't serialize this reference type: '{0}'\nFor field: '{1}'", field.FieldType, field.Name));
						}
						else
						{
							output += string.Format("{0}:{1};", name, val);
						}
					}
				}

				output = output.TrimEnd(';');
				output += ")";

				return output;
			}

			//Deserialize without knowing type
			public static object Deserialize(string data, object[] args = null)
			{
				//extract serialized class name
				var index = data.IndexOf('(');
				var serializedClassName = data.Substring(0, index);

				//fetch all serialized classes names, and try to match it
				Type type = null;
				var allTypes = typeof(Serialization).Assembly.GetTypes();
				foreach (var t in allTypes)
				{
					var classAttributes = t.GetCustomAttributes(typeof(SerializeAsAttribute), false);
					if (classAttributes != null && classAttributes.Length == 1)
					{
						var name = (classAttributes[0] as SerializeAsAttribute).serializedName;
						if (name == serializedClassName)
						{
							//match!
							type = t;
						}
					}
				}

				if (type == null)
				{
					Debug.LogError(ShaderGenerator2.ErrorMsg("Can't find proper Type for serialized class named '<b>" + serializedClassName + "</b>'"));
					return null;
				}

				//return new object with correct type
				return Deserialize(data, type, args);
			}

			//Deserialize to a new object (needs a new() constructor, and valid arguments as 'args', if any)
			public static object Deserialize(string data, Type type, object[] args = null)
			{
				var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
				foreach(var method in methods)
				{
					var deserializeCallbacks = method.GetCustomAttributes(typeof(CustomDeserializeCallbackAttribute), false);
					if (deserializeCallbacks.Length > 0)
					{
						return method.Invoke(null, new object[] { data, args });
					}
				}

				var obj = Activator.CreateInstance(type, args);
				return DeserializeTo(obj, data, type, args);
			}

			//Deserialize a specific type
			//'specialClasses': hook so that the caller can implement its own deserialization logic (used for Shader Property list in Config)
			public static object DeserializeTo(object obj, string data, Type type, object[] args = null, Dictionary<Type, Func<object, string, object>> specialClasses = null)
			{
				//extract parts of the input data, format should be "type(field:value;field2:value)"
				var index = data.IndexOf('(');

				var serializedClassName = data.Substring(0, index);
				var fieldsData = data.Substring(index + 1);
				fieldsData = fieldsData.Substring(0, fieldsData.Length - 1);    //remove trailing ')'

				//fetch class serialized name and check against specified T type
				var classAttributes = type.GetCustomAttributes(typeof(SerializeAsAttribute), false);
				if (classAttributes != null && classAttributes.Length == 1)
				{
					var name = (classAttributes[0] as SerializeAsAttribute).serializedName;
					if (name != serializedClassName)
					{
						Debug.LogError(string.Format("Class doesn't match serialized class name.\nExpected '{0}', got '{1}'.", serializedClassName, name));
						return null;
					}
				}

				//fetch all [SerializeAs] fields from that type
				var fields = new List<FieldInfo>(type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
				var serializedFields = new Dictionary<string, FieldInfo>();
				foreach (var field in fields)
				{
					var attributes = field.GetCustomAttributes(typeof(SerializeAsAttribute), true);
					if (attributes != null && attributes.Length == 1)
					{
						var name = (attributes[0] as SerializeAsAttribute).serializedName;
						serializedFields.Add(name, field);
					}
				}

				//converts a serialized string into a value
				Func<string, Type, object> StringToValue = null;
				StringToValue = (strValue, t) =>
				{
					//special classes: call the callback specified by caller
					if (specialClasses != null && specialClasses.ContainsKey(t))
					{
						return specialClasses[t].Invoke(obj, strValue);
					}

					//object types
					if (!t.IsValueType && t != typeof(string))
					{
						// handle null values
						if (strValue == "__NULL__")
						{
							return null;
						}

						//list
						if (typeof(IList).IsAssignableFrom(t))
						{
							//parse list values: remove 'list[' and ']' characters, and split on ','
							var serializedValues = SplitExcludingBlocks(strValue.Substring(5, strValue.Length - 6), ',', true, "()", "[]");

							//find what T is for this List<T>
							var itemType = t.GetGenericArguments()[0];

							//create new list with parsed serialized values
							var genericListType = typeof(List<>).MakeGenericType(itemType);
							var list = (IList)Activator.CreateInstance(genericListType);
							foreach (var item in serializedValues)
							{
								if (string.IsNullOrEmpty(item))
									continue;

								var v = StringToValue(item, itemType);
								if (v != null)
									list.Add(v);
							}

							//assign new list for obj
							return list;
						}

						//dict
						if (typeof(IDictionary).IsAssignableFrom(t))
						{
							//parse dict values: remove 'dict[' and ']' characters, and split on ','
							var serializedValues = SplitExcludingBlocks(strValue.Substring(5, strValue.Length - 6), ',', true, "()", "[]");

							//find what kind of KeyValuePair types are used
							var keyType = t.GetGenericArguments()[0];
							var valueType = t.GetGenericArguments()[1];

							//create new dictionary with parsed serialized values
							var genericDictType = typeof(Dictionary<,>).MakeGenericType(keyType, valueType);
							var dict = (IDictionary)Activator.CreateInstance(genericDictType);
							foreach (var item in serializedValues)
							{
								if (string.IsNullOrEmpty(item))
									continue;

								//gey key & value from format "key=value"
								var kv = item.Split('=');
								var key = kv[0];
								var value = kv[1];

								var k = StringToValue(key, keyType);
								var v = StringToValue(value, valueType);

								if (k != null && v != null)
									dict.Add(k, v);
							}

							//assign new list for obj
							return dict;
						}
						//else try to deserialize
						{
							var value = Deserialize(strValue, t, args);
							return value;
						}
					}

					//Unity value-type structs
					if (t == typeof(Vector2))
					{
						var v2data = strValue.Substring(1, strValue.Length - 2).Split(',');
						return new Vector2(float.Parse(v2data[0], CultureInfo.InvariantCulture), float.Parse(v2data[1], CultureInfo.InvariantCulture));
					}

					if (t == typeof(Vector3))
					{
						var v3data = strValue.Substring(1, strValue.Length - 2).Split(',');
						return new Vector3(float.Parse(v3data[0], CultureInfo.InvariantCulture), float.Parse(v3data[1], CultureInfo.InvariantCulture), float.Parse(v3data[2], CultureInfo.InvariantCulture));
					}

					if (t == typeof(Vector4))
					{
						var v4data = strValue.Substring(1, strValue.Length - 2).Split(',');
						return new Vector4(float.Parse(v4data[0], CultureInfo.InvariantCulture), float.Parse(v4data[1], CultureInfo.InvariantCulture), float.Parse(v4data[2], CultureInfo.InvariantCulture), float.Parse(v4data[3], CultureInfo.InvariantCulture));
					}

					if (t == typeof(Color))
					{
						var cData = strValue.Substring("RGBA(".Length, strValue.Length - "RGBA(".Length - 1).Split(',');
						return new Color(float.Parse(cData[0], CultureInfo.InvariantCulture), float.Parse(cData[1], CultureInfo.InvariantCulture), float.Parse(cData[2], CultureInfo.InvariantCulture), float.Parse(cData[3], CultureInfo.InvariantCulture));
					}

					//enums
					if (typeof(Enum).IsAssignableFrom(t))
					{
						return Enum.Parse(t, strValue);
					}

					//string: remove quotes to extract value
					if (t == typeof(string))
					{
						// handle null values
						if (strValue == "__NULL__")
						{
							return null;
						}

						return strValue.Trim('"');
					}

					//value type: automatic conversion
					return Convert.ChangeType(strValue, t, CultureInfo.InvariantCulture);
				};

				//iterate through entries in the source string
				var entries = SplitExcludingBlocks(fieldsData, ';', true, "()", "[]");
				foreach (var entry in entries)
				{
					var kvp = SplitExcludingBlocks(entry, ':', true, "()");
					var name = kvp[0];
					var strValue = kvp[1];

					if (serializedFields.ContainsKey(name))
					{
						var fieldInfo = serializedFields[name];
						var v = StringToValue(strValue, fieldInfo.FieldType);
						if (v != null)
							fieldInfo.SetValue(obj, v);
					}
				}

				//on deserialize callback, if any
				List<MethodInfo> methods = new List<MethodInfo>(type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
				foreach (var method in methods)
				{
					var deserializedAttributes = method.GetCustomAttributes(typeof(OnDeserializeCallbackAttribute), false);
					if (deserializedAttributes != null && deserializedAttributes.Length > 0)
					{
						//invoke the OnDeserialize callback
						method.Invoke(obj, null);
					}
				}

				return obj;
			}

			//Split a string excluding any characters found inside specified blocks
			//e.g.
			//  splitExcludingBlocks("list(a,b,c),list(d,e),list(f,g,h)", "()") will return
			//will return
			//  list(a,b,c)   list(d,e)   list(f,g,h)
			//and not
			// list(a   b   c)   list(d   e   list(f   g   h
			public static string[] SplitExcludingBlocks(string input, char separator, params string[] blocks) { return SplitExcludingBlocks(input, separator, false, false, blocks); }
			public static string[] SplitExcludingBlocks(string input, char separator, bool excludeQuotes, params string[] blocks) { return SplitExcludingBlocks(input, separator, excludeQuotes, false, blocks); }
			public static string[] SplitExcludingBlocks(string input, char separator, bool excludeQuotes, bool removeEmptyEntries, params string[] blocks)
			{
				foreach (var block in blocks)
				{
					if(block == "\"\"")
					{
						Debug.LogWarning("Using quotes block \"\" -> use excludeQuotes=true instead!");
					}
				}

				var insideBlock = 0;
				var insideQuotes = false;
				var i = 0;
				var currentWord = new StringBuilder();
				var words = new List<string>();

				//get opening/ending chars for blocks
				var openingChars = new List<char>(blocks.Length);
				var closingChars = new List<char>(blocks.Length);
				foreach (var block in blocks)
				{
					openingChars.Add(block[0]);
					closingChars.Add(block[1]);
				}

				while (i < input.Length)
				{
					if (!insideQuotes)
					{
						if (openingChars.Contains(input[i]))
							insideBlock++;
						else if (closingChars.Contains(input[i]))
							insideBlock--;
					}

					if (excludeQuotes && input[i] == '"')
					{
						insideQuotes = !insideQuotes;
						insideBlock += insideQuotes ? +1 : -1;
					}

					if (input[i] == separator && insideBlock == 0)
					{
						if (!removeEmptyEntries || currentWord.Length != 0)
						{
							words.Add(currentWord.ToString());
						}
						currentWord.Length = 0;
					}
					else
					{
						currentWord.Append(input[i]);
					}

					i++;
				}

				if (!removeEmptyEntries || currentWord.Length != 0)
				{
					words.Add(currentWord.ToString());
				}

				return words.ToArray();
			}
		}
	}
}

// -----------------------------------------------------------------------------
// Merged from GlobalOptions.cs
// -----------------------------------------------------------------------------

// Represents the global options for the Shader Generator, using the EditorPrefs API

namespace ToonyColorsPro
{
	namespace ShaderGenerator
	{
		// Global Options shared across all Unity projects
		public static class GlobalOptions
		{
			[System.Serializable]
			public class Data
			{
				public bool ShowOptions = true;
				public bool ShowDisabledFeatures = true;
				public bool SelectGeneratedShader = true;
				public bool ShowContextualHelp = true;
				public bool DockableWindow = false;
			}
			static Data _data;
			public static Data data
			{
				get
				{
					if (_data == null)
					{
						LoadUserPrefs();
					}
					return _data;
				}
			}

			public static void LoadUserPrefs()
			{
				string dataStr = EditorPrefs.GetString("TCP2_GlobalOptions", null);
				_data = new Data();
				if (!string.IsNullOrEmpty(dataStr))
				{
					EditorJsonUtility.FromJsonOverwrite(dataStr, _data);
				}
			}

			public static void SaveUserPrefs()
			{
				EditorPrefs.SetString("TCP2_GlobalOptions", EditorJsonUtility.ToJson(data));
			}
		}

		// Project Options only saved for this Unity project
		public static class ProjectOptions
		{
			[System.Serializable]
			public class Data
			{
				public bool AutoNames = true;
				public bool SubFolders = true;
				public bool OverwriteConfig = false;
				public bool LoadAllShaders = false;
				public string CustomOutputPath = ShaderGenerator2.OUTPUT_PATH;
				public string LastImplementationExportImportPath = Application.dataPath;
				public List<string> OpenedFoldouts = new List<string>();
				public bool UseCustomFont = false;
				public Font CustomFont = null;
				public bool CustomFontInitialized = false;
				public bool Upgrade_Hybrid1toHybrid2_Done = false;
			}
			static Data _data;
			public static Data data
			{
				get
				{
					if (_data == null)
					{
						LoadProjectOptions();
					}
					return _data;
				}
			}

			static string GetPath()
			{
				return Application.dataPath.Replace(@"\","/") + "/../ProjectSettings/ToonyColorsPro.json";
			}

			public static void LoadProjectOptions()
			{
				_data = new Data();
				string path = GetPath();
				if (File.Exists(path))
				{
					string json = File.ReadAllText(path);
					EditorJsonUtility.FromJsonOverwrite(json, _data);
				}
			}

			public static void SaveProjectOptions()
			{
				string path = GetPath();
				string json = EditorJsonUtility.ToJson(_data, true);
				File.WriteAllText(path, json);
			}
		}
	}
}

// -----------------------------------------------------------------------------
// Merged from Module.cs
// -----------------------------------------------------------------------------

// Represents a Shader Generator 2 module: external file that has specific code for a feature, that can be reused among templates

namespace ToonyColorsPro
{
	namespace ShaderGenerator
	{
		public class Module
		{
			public class Argument
			{
				public string name;

				//Variable type is parsed but we actually don't care about it in the code, it's just an indication in the Module file for proper integration into the Template
				public string variable;

				public override string ToString()
				{
					return string.Format("{0} : {1}", name, variable);
				}
			}

			public string name;
			public string[] Features = new string[0];
			public string[] PropertiesNew = new string[0];
			public string[] Keywords = new string[0];
			public string[] ShaderFeaturesBlock = new string[0];
			public string[] PropertiesBlock = new string[0];
			public string[] Functions = new string[0];
			public bool ExplicitFunctionsDeclaration;
			public string[] Variables = new string[0];
			public string[] VariablesOutsideCBuffer = new string[0];
			public string[] InputStruct = new string[0];
			Dictionary<string, string[]> Vertices = new Dictionary<string, string[]>();
			Dictionary<string, string[]> Fragments = new Dictionary<string, string[]>();

			Dictionary<string, Argument[]> VerticesArgs = new Dictionary<string, Argument[]>();
			Dictionary<string, Argument[]> FragmentsArgs = new Dictionary<string, Argument[]>();
			
			Dictionary<string, List<string>> ArbitraryBlocks = new Dictionary<string, List<string>>();

			public List<string> GetArbitraryBlock(string block)
			{
				if (!ArbitraryBlocks.ContainsKey(block))
				{
					Debug.LogError(string.Format("Couldn't find block with name '{0}' in module '{1}'", block, this.name));
					return null;
				}

				return this.ArbitraryBlocks[block];
			}

			static public Module CreateFromName(string moduleName)
			{
				string moduleFile = string.Format("Module_{0}.txt", moduleName);
				string rootPath = Utils.FindReadmePath(true);
				string modulePath = string.Format("{0}/Shader Templates 2/Modules/{1}", rootPath, moduleFile);

				var textAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(modulePath);
				string moduleText = textAsset != null ? textAsset.text : null;

				//Can't find through default path, try to search for the file using AssetDatabase
				if(moduleText == null)
				{
					var matches = AssetDatabase.FindAssets(string.Format("Module_{0} t:textasset", moduleName));
					if (matches.Length > 0)
					{
						// Get the first result
						textAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(AssetDatabase.GUIDToAssetPath(matches[0]));
						moduleText = textAsset != null ? textAsset.text : null;
					}

					if (moduleText == null)
					{
						moduleText = LoadBundledModule(rootPath, moduleFile);
					}

					if (moduleText == null)
					{
						Debug.LogError(ShaderGenerator2.ErrorMsg(string.Format("Can't find module using Unity's search system. Make sure that the file 'Module_{0}' or 'SG2_Modules.txt' is in the project!", moduleName)));
					}
				}

				if(moduleText == null)
				{
					Debug.LogError(ShaderGenerator2.ErrorMsg(string.Format("Can't load module: '{0}'", moduleName)));
					return null;
				}

				var lines = moduleText.Split(new string[] { "\r\n", "\n" }, System.StringSplitOptions.None);

				List<string> features = new List<string>();
				List<string> propertiesNew = new List<string>();
				List<string> keywords = new List<string>();
				List<string> shaderFeaturesBlock = new List<string>();
				List<string> propertiesBlock = new List<string>();
				List<string> variables = new List<string>();
				List<string> variablesOutsideCbuffer = new List<string>();
				List<string> functions = new List<string>();
				List<string> inputStruct = new List<string>();
				bool explicitFunctions = false;

				Dictionary<string, List<Argument>> verticesArgs = new Dictionary<string, List<Argument>>();
				Dictionary<string, List<Argument>> fragmentsArgs = new Dictionary<string, List<Argument>>();
				Dictionary<string, List<string>> vertices = new Dictionary<string, List<string>>();
				Dictionary<string, List<string>> fragments = new Dictionary<string, List<string>>();
				
				Dictionary<string, List<string>> arbitraryBlocks = new Dictionary<string, List<string>>();

				List<string> currentList = null;

				foreach (var line in lines)
				{
					if(line.StartsWith("#") && !line.Contains("_IMPL"))
					{
						var lineTrim = line.Trim();

						//fragment can have arguments, so check the start of the line instead of exact word
						if(lineTrim.StartsWith("#VERTEX"))
						{
							var key = "";
							if(lineTrim.Contains(":"))
							{
								int start = "#VERTEX:".Length;
								int end = lineTrim.IndexOf('(');
								key = lineTrim.Substring(start, end - start);
							}

							currentList = new List<string>();
							vertices.Add(key, currentList);

							if (lineTrim.Contains("(") && lineTrim.Contains(")"))
							{
								//parse arguments
								var vertexArgs = ParseArguments(lineTrim);
								verticesArgs.Add(key, vertexArgs);
							}
						}
						//#LIGHTING is an alias for fragment here, just to differentiate in the template code
						else if(lineTrim.StartsWith("#FRAGMENT") || lineTrim.StartsWith("#LIGHTING"))
						{
							var key = "";
							if (lineTrim.Contains(":"))
							{
								int start = "#FRAGMENT:".Length; // same character count for #LIGHTING
								int end = lineTrim.IndexOf('(');
								if(end >= 0)
									key = lineTrim.Substring(start, end - start);
								else
									key = lineTrim.Substring(start);
							}

							currentList = new List<string>();
							fragments.Add(key, currentList);

							if(lineTrim.Contains("(") && lineTrim.Contains(")"))
							{
								//parse arguments
								var fragmentArgs = ParseArguments(lineTrim);
								fragmentsArgs.Add(key, fragmentArgs);
							}
						}
						else if (lineTrim.StartsWith("#FUNCTIONS:EXPLICIT"))
						{
							// Explicit functions that have to be declared in the template with [[Module:FUNCTIONS:module_name]]
							currentList = functions;
							explicitFunctions = true;
						}
						else
						{
							switch(lineTrim)
							{
								case "#FEATURES":              			currentList = features; break;
								case "#PROPERTIES_NEW":        			currentList = propertiesNew; break;
								case "#KEYWORDS":              			currentList = keywords; break;
								case "#PROPERTIES_BLOCK":      			currentList = propertiesBlock; break;
								case "#SHADER_FEATURES_BLOCK": 			currentList = shaderFeaturesBlock; break;
								case "#FUNCTIONS":             			currentList = functions; break;
								case "#VARIABLES":             			currentList = variables; break;
								case "#VARIABLES_OUTSIDE_CBUFFER":     	currentList = variablesOutsideCbuffer; break;
								case "#INPUT":                         	currentList = inputStruct; break;
								case "#END":                           	currentList = null; break;
								default:
								{
									// An "arbitrary block" is parsed if not using a predefine keyword like above, and we are not iterating over an existing block
									if (currentList == null)
									{
										string block = lineTrim.Substring(1);
										if (block.Length > 0 && !char.IsWhiteSpace(block[0]))
										{
											currentList = new List<string>();
											arbitraryBlocks.Add(block, currentList);
										}
									}
									break;
								}
							}
						}
					}
					else
					{
						if(currentList != null)
						{
							currentList.Add(line);
						}
					}
				}

				Module module = new Module();
				module.name = moduleName;
				module.Features = features.ToArray();
				module.PropertiesNew = propertiesNew.ToArray();
				module.Keywords = keywords.ToArray();
				module.ShaderFeaturesBlock = shaderFeaturesBlock.ToArray();
				module.PropertiesBlock = propertiesBlock.ToArray();
				module.Functions = functions.ToArray();
				module.Variables = variables.ToArray();
				module.VariablesOutsideCBuffer = variablesOutsideCbuffer.ToArray();
				module.InputStruct = inputStruct.ToArray();
				module.ExplicitFunctionsDeclaration = explicitFunctions;
				module.ArbitraryBlocks = arbitraryBlocks;

				// #VERTEX
				if (vertices.Count == 0)
				{
					vertices.Add("", new List<string>());
					verticesArgs.Add("", new List<Argument>());
				}

				foreach (var vertexPair in vertices)
				{
					var key = vertexPair.Key;
					module.Vertices.Add(key, vertexPair.Value.ToArray());
					if (verticesArgs.ContainsKey(key))
					{
						module.VerticesArgs.Add(key, verticesArgs[key].ToArray());
					}
				}

				// #FRAGMENT
				if (fragments.Count == 0)
				{
					fragments.Add("", new List<string>());
					fragmentsArgs.Add("", new List<Argument>());
				}
				
				foreach (var fragmentPair in fragments)
				{
					var key = fragmentPair.Key;
					module.Fragments.Add(key, fragmentPair.Value.ToArray());
					if (fragmentsArgs.ContainsKey(key))
					{
						module.FragmentsArgs.Add(key, fragmentsArgs[key].ToArray());
					}
				}

				module.ProcessIndentation();

				return module;
			}

			static List<Argument> ParseArguments(string line)
			{
				var list = new List<Argument>();

				//parse arguments
				int start = line.IndexOf("(")+1;
				int end = line.IndexOf(")");
				var content = line.Substring(start, end-start);
				var args = content.Split(',');
				for(int i = 0; i < args.Length; i++)
				{
					var arg = args[i].Trim();
					int spaceIndex = arg.IndexOf(arg.Substring(arg.IndexOf(' ')));
					var type = arg.Substring(0, spaceIndex);
					var name = arg.Substring(spaceIndex+1);
					var argument = new Argument()
					{
						variable = type,
						name = name
					};
					list.Add(argument);
				}
				return list;
			}

			static string LoadBundledModule(string rootPath, string moduleFile)
			{
				var bundledPath = string.Format("{0}/Shader Templates 2/SG2_Modules.txt", rootPath);
				var bundledAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(bundledPath);
				if (bundledAsset == null)
				{
					var matches = AssetDatabase.FindAssets("SG2_Modules t:textasset");
					if (matches.Length > 0)
					{
						bundledAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(AssetDatabase.GUIDToAssetPath(matches[0]));
					}
				}

				if (bundledAsset == null)
				{
					return null;
				}

				string startMarker = string.Format("//#MODULE_FILE:{0}", moduleFile);
				const string endMarker = "//#END_MODULE_FILE";
				var text = bundledAsset.text;
				int start = text.IndexOf(startMarker, StringComparison.Ordinal);
				if (start < 0)
				{
					return null;
				}

				start += startMarker.Length;
				while (start < text.Length && (text[start] == '\r' || text[start] == '\n'))
				{
					start++;
				}

				int end = text.IndexOf(endMarker, start, StringComparison.Ordinal);
				if (end < 0)
				{
					return null;
				}

				return text.Substring(start, end - start).Trim();
			}

			//Find minimum indentation and remove for every line for each block
			void ProcessIndentation()
			{
				RemoveMinimumIndentation(this.Features);
				RemoveMinimumIndentation(this.PropertiesNew);
				RemoveMinimumIndentation(this.Keywords);
				RemoveMinimumIndentation(this.ShaderFeaturesBlock);
				RemoveMinimumIndentation(this.PropertiesBlock);
				RemoveMinimumIndentation(this.Functions);
				RemoveMinimumIndentation(this.Variables);
				RemoveMinimumIndentation(this.VariablesOutsideCBuffer);
				RemoveMinimumIndentation(this.InputStruct);
				RemoveMinimumIndentation(this.Vertices);
				RemoveMinimumIndentation(this.Fragments);
			}

			void RemoveMinimumIndentation(Dictionary<string, string[]> dict)
			{
				foreach (var key in dict.Keys)
				{
					RemoveMinimumIndentation(dict[key]);
				}
			}

			void RemoveMinimumIndentation(string[] block)
			{
				if(block == null)
					return;

				//Find minimum number of leading tabs across all lines
				int minIndent = 999;
				for (int i = 0; i < block.Length; i++)
				{
					string trimmedBlock = block[i].Trim();
					if (trimmedBlock.StartsWith("///") || block[i].StartsWith("#") || string.IsNullOrEmpty(trimmedBlock))
					{
						continue;
					}

					// special cases to ignore, as they won't be part of the shader code
					if (trimmedBlock[0] == '#' && trimmedBlock.Contains("not_empty"))
					{
						continue;
					}

					int j = 0;
					while(j < block[i].Length && block[i][j] == '\t')
					{
						j++;
					}
					minIndent = Mathf.Min(minIndent, j);
				}

				//Remove that minimum value for all lines (excluding /// and ENABLE_IMPL and DISABLE_IMPL)
				for(int i = 0; i < block.Length; i++)
				{
					string trim = block[i].Trim();
					if (trim.StartsWith("///") || (trim.StartsWith("#") && trim.Contains("_IMPL")))
						continue;

					if (trim.StartsWith("#") && trim.Contains("not_empty"))
						continue;

					if (block[i].Length > minIndent)
						block[i] = block[i].Substring(minIndent);
				}
			}

			//Return the Vertex Lines with the arguments replaced with their proper names
			public string[] VertexLines(List<string> arguments, string key = "")
			{
				Argument[] args;
				VerticesArgs.TryGetValue(key, out args);
				return ArgumentLines(Vertices[key], args, arguments);
			}

			//Return the Fragment Lines with the arguments replaced with their proper names
			public string[] FragmentLines(List<string> arguments, string key = "")
			{
				Argument[] args;
				string[] lines;
				FragmentsArgs.TryGetValue(key, out args);
				Fragments.TryGetValue(key, out lines);

				if (lines == null)
				{
					Debug.LogError(ShaderGenerator2.ErrorMsg(string.Format("Can't find #FRAGMENT/#LIGHTING for Module '{0}{1}'", this.name, string.IsNullOrEmpty(key) ? "" : ":" + key)));
					return null;
				}

				return ArgumentLines(lines, args, arguments);
			}

			string[] ArgumentLines(string[] array, Argument[] arguments, List<string> suppliedArguments)
			{
				if(arguments == null || arguments.Length == 0)
					return array;
				else
				{
					if(suppliedArguments.Count != arguments.Length)
					{
						Debug.LogError(ShaderGenerator2.ErrorMsg(string.Format("[Module {4}] Invalid number of arguments provided: got <b>{0}</b>, expected <b>{1}</b>:\nExpected: {2}\nSupplied: {3}",
							suppliedArguments.Count,
							arguments.Length,
							string.Join(", ", System.Array.ConvertAll(arguments, a => a.ToString())),
							string.Join(", ", suppliedArguments.ToArray()),
							this.name)));
					}

					var list = new List<string>();
					foreach(var line in array)
					{
						string lineWithArgs = line;
						for(int i = 0; i < arguments.Length; i++)
						{
							lineWithArgs = System.Text.RegularExpressions.Regex.Replace(lineWithArgs, @"\b" + arguments[i].name + @"\b", suppliedArguments[i]);
						}
						list.Add(lineWithArgs);
					}

					return list.ToArray();
				}
			}
		}
	}
}

// -----------------------------------------------------------------------------
// Merged from MaterialLayer.cs
// -----------------------------------------------------------------------------

namespace ToonyColorsPro
{
    namespace  ShaderGenerator
    {
        [Serialization.SerializeAs("ml")]
        public class MaterialLayer
        {
            public enum BlendType
            {
                LinearInterpolation = 0,
                // NormalMap,
                Add,
                Multiply,
                MultiplyDouble,
                Custom = 255
            }
            
            [Serialization.SerializeAs("uid")] public readonly string uid;
            [Serialization.SerializeAs("name")] public string name = "Material Layer";
            [Serialization.SerializeAs("src"), Serialization.ForceSerialization] public ShaderProperty sourceShaderProperty;
            [Serialization.SerializeAs("use_contrast")] bool useContrastProperty;
            [Serialization.SerializeAs("ctrst"), Serialization.ForceSerialization] public ShaderProperty contrastProperty;
            [Serialization.SerializeAs("use_noise")] bool useNoiseProperty;
            [Serialization.SerializeAs("noise"), Serialization.ForceSerialization] public ShaderProperty noiseProperty;
            internal bool expanded;

            internal bool UseContrastProperty
            {
                get { return useContrastProperty; }
                set
                {
                    useContrastProperty = value;
                    if (useContrastProperty)
                    {
                        if (contrastProperty == null)
                        {
                            contrastProperty = new ShaderProperty("contrast_" + uid, ShaderProperty.VariableType.@float);
                            contrastProperty.materialLayerUid = uid;
                            contrastProperty.SetDefaultImplementations(new ShaderProperty.Implementation[]
                            {
                                new ShaderProperty.Imp_MaterialProperty_Range(contrastProperty)
                                {
                                    Label = "Contrast",
                                    Min = 0,
                                    Max = 1,
                                    DefaultValue = 0.5f
                                }
                            });
                            contrastProperty.DisplayName = uid + " Layer Contrast";
                        }
                    }
                    else
                    {
                        contrastProperty = null;
                    }
                }
            }

            internal bool UseNoiseProperty
            {
                get { return useNoiseProperty; }
                set
                {
                    useNoiseProperty = value;
                    if (useNoiseProperty)
                    {
                        if (noiseProperty == null)
                        {
                            noiseProperty = new ShaderProperty("noise_" + uid, ShaderProperty.VariableType.@float);
                            noiseProperty.materialLayerUid = uid;
                            noiseProperty.SetDefaultImplementations(new ShaderProperty.Implementation[]
                            {
                                new ShaderProperty.Imp_CustomCode(noiseProperty)
                                {
                                    code = "saturate( {2}.r * {3} ) - {3} / 2.0"
                                },
                                new ShaderProperty.Imp_MaterialProperty_Texture(noiseProperty)
                                {
                                    Label = "Noise Texture",
                                    PropertyName = string.Format("_NoiseTexture_{0}", uid),
                                    DefaultValue = "gray"
                                },
                                new ShaderProperty.Imp_MaterialProperty_Range(noiseProperty)
                                {
                                    Label = "Noise Strength",
                                    PropertyName = string.Format("_NoiseStrength_{0}", uid),
                                    Min = 0,
                                    Max = 1,
                                    DefaultValue = 0.1f
                                }
                            });
                            noiseProperty.DisplayName = uid + " Layer Noise";
                        }
                    }
                    else
                    {
                        noiseProperty = null;
                    }
                }
            }

            [Serialization.CustomDeserializeCallback]
            static MaterialLayer Deserialize(string data, object[] args)
            {
                var materialLayer = new MaterialLayer();
                
                // custom callback for ShaderProperty
                Func<object, string, object> onDeserializeShaderProperty = (spObj, spData) =>
                {
                    if (spData == "__NULL__")
                    {
                        return null;
                    }
                    
                    // HACK figure out which property is being deserialized based on name
                    // substring(11) will strip:  sp(name:"
                    ShaderProperty targetProperty = materialLayer.sourceShaderProperty;
                    if (spData.Substring(9).StartsWith("contrast_"))
                    {
                        // Can't deserialize to null, so we need to create the Shader Property first
                        materialLayer.contrastProperty = new ShaderProperty("temp_contrast", ShaderProperty.VariableType.@float);
                        targetProperty = materialLayer.contrastProperty;
                    }
                    if (spData.Substring(9).StartsWith("noise_"))
                    {
                        // Can't deserialize to null, so we need to create the Shader Property first
                        materialLayer.noiseProperty = new ShaderProperty("temp_noise", ShaderProperty.VariableType.@float);
                        targetProperty = materialLayer.noiseProperty;
                    }

                    if (targetProperty == null)
                    {
                        return null;
                    }

                    // custom callback for Implementations
                    Func<object, string, object> onDeserializeImplementation = (impObj, impData) =>
                    {
                        return ShaderGenerator2.CurrentConfig.DeserializeImplementationHandler(impObj, impData, targetProperty);
                    };
                    var implementationHandling = new Dictionary<Type, Func<object, string, object>> { { typeof(ShaderProperty.Implementation), onDeserializeImplementation } };
                    
                    return Serialization.DeserializeTo(targetProperty, spData, typeof(ShaderProperty), null, implementationHandling);
                };
                var shaderPropertyHandling = new Dictionary<Type, Func<object, string, object>> { { typeof(ShaderProperty), onDeserializeShaderProperty } };

                return (MaterialLayer)Serialization.DeserializeTo(materialLayer, data, typeof(MaterialLayer), null, shaderPropertyHandling);
            }

            [Serialization.OnDeserializeCallback]
            void OnDeserialized()
            {
                sourceShaderProperty.materialLayerUid = uid;
                sourceShaderProperty.DisplayName = uid + " Source";
                if (contrastProperty != null)
                {
                    contrastProperty.DisplayName = uid + " Layer Contrast";
                    contrastProperty.materialLayerUid = uid;
                }
                if (noiseProperty != null)
                {
                    noiseProperty.DisplayName = uid + " Layer Noise";
                    noiseProperty.materialLayerUid = uid;
                }
            }

            public MaterialLayer()
            {
                uid = GenerateUID();
                sourceShaderProperty = new ShaderProperty("layer_" + uid, ShaderProperty.VariableType.@float);
                sourceShaderProperty.materialLayerUid = uid;
                sourceShaderProperty.SetDefaultImplementations(new ShaderProperty.Implementation[]
                {
                    new ShaderProperty.Imp_MaterialProperty_Texture(sourceShaderProperty)
                    {
                        Label = "Source Texture"
                    }
                });
                sourceShaderProperty.DisplayName = uid + " Source";
            }

            internal string GetVariableName()
            {
                return ShaderProperty.ToLowerCamelCase(name);
            }
            
            internal string PrintSourceProperties(string indent)
            {
                string output = sourceShaderProperty.PrintProperties(indent);;
                if (UseContrastProperty)
                {
                    output += "\n" + indent + contrastProperty.PrintProperties(indent);
                }
                if (useNoiseProperty)
                {
                    output += "\n" + indent + noiseProperty.PrintProperties(indent);
                }
                return output;
            }

            static string GenerateUID()
            {
                string uid;
                bool valid = true;
                
                do
                {
                    uid = Random.Range(0x100000, 0xFFFFFF).ToString("x");
                    foreach (var materialLayer in ShaderGenerator2.CurrentConfig.materialLayers)
                    {
                        if (materialLayer.uid == uid)
                        {
                            valid = false;
                            break;
                        }
                    }
                } while (!valid);

                return uid;
            }

            internal void ShowPresetsMenu()
            {
                var menu = new GenericMenu();
                foreach (var preset in Presets)
                {
                    menu.AddItem(preset, false, OnSelectPreset, preset.text); 
                }
                menu.ShowAsContext();
            }

            void OnSelectPreset(object presetObj)
            {
                bool ok = EditorUtility.DisplayDialog("Load Source Preset", "Warning: this will replace all implementations and custom settings for the Source property of this layer.", "Ok", "Cancel");
                if (!ok)
                {
                    return;
                }

                foreach (var implementation in sourceShaderProperty.implementations)
                {
                    var imp_mp = implementation as ShaderProperty.Imp_MaterialProperty;
                    if (imp_mp != null)
                    {
                        imp_mp.ignoreUniquePropertyName = true;
                    }
                }
                
                var imps = CreateImplementationsFromPreset(presetObj as string, this.sourceShaderProperty);
                if (imps != null)
                {
                    this.sourceShaderProperty.SetDefaultImplementations(imps);
                    this.sourceShaderProperty.expanded = true;
                }
                else
                {
                    Debug.LogError("Couldn't create implementations from preset: " + presetObj);
                }
            }

            ShaderProperty.Implementation[] CreateImplementationsFromPreset(string method, ShaderProperty shaderProperty)
            {
                ShaderProperty.Implementation[] imps = null;
                switch (method)
                {
                    case "Normal-Based/Local/X":
                    case "Normal-Based/Local/Y":
                    case "Normal-Based/Local/Z":
                    case "Normal-Based/World/X":
                    case "Normal-Based/World/Y":
                    case "Normal-Based/World/Z":
                    {
                        bool worldSpace = method.Contains("World");
                        char axis = char.ToLowerInvariant(method[method.Length - 1]);

                        ShaderProperty.Implementation imp_normal = null;
                        if (worldSpace)
                        {
                            imp_normal = new ShaderProperty.Imp_WorldNormal(shaderProperty) { Channels = method[method.Length - 1].ToString() };
                        }
                        else
                        {
                            imp_normal = new ShaderProperty.Imp_LocalNormal(shaderProperty) { Channels = method[method.Length - 1].ToString() };
                        }

                        imps = new ShaderProperty.Implementation[]
                        {
                            new ShaderProperty.Imp_CustomCode(shaderProperty) { code = "{2}." + axis + " + {3}" },
                            imp_normal,
                            new ShaderProperty.Imp_MaterialProperty_Float(shaderProperty)
                            {
                                Label = "Normal Threshold",
                                PropertyName = string.Format("_NormalThreshold_{0}", uid)
                            }
                        };
                        break;
                    }

                    case "Position-Based/Local/X":
                    case "Position-Based/Local/Y":
                    case "Position-Based/Local/Z":
                    case "Position-Based/World/X":
                    case "Position-Based/World/Y":
                    case "Position-Based/World/Z":
                    {
                        bool worldSpace = method.Contains("World");
                        char axis = char.ToLowerInvariant(method[method.Length - 1]);

                        ShaderProperty.Implementation imp_position = null;
                        if (worldSpace)
                        {
                            imp_position = new ShaderProperty.Imp_WorldPosition(shaderProperty) { Channels = method[method.Length - 1].ToString() };
                        }
                        else
                        {
                            imp_position = new ShaderProperty.Imp_LocalPosition(shaderProperty) { Channels = method[method.Length - 1].ToString() };
                        }

                        imps = new ShaderProperty.Implementation[]
                        {
                            new ShaderProperty.Imp_CustomCode(shaderProperty) { code = "( {2}." + axis + " * {4} ) + {3}" },
                            imp_position,
                            new ShaderProperty.Imp_MaterialProperty_Float(shaderProperty)
                            {
                                Label = "Position Threshold",
                                PropertyName = string.Format("_PositionThreshold_{0}", uid)
                            },
                            new ShaderProperty.Imp_MaterialProperty_Float(shaderProperty)
                            {
                                Label = "Position Range",
                                PropertyName = string.Format("_PositionRange_{0}", uid),
                            }
                        };
                        break;
                    }
                    
                    case "Vertex Colors/R":
                    case "Vertex Colors/G":
                    case "Vertex Colors/B":
                    case "Vertex Colors/A":
                    {
                        char channel = char.ToLowerInvariant(method[method.Length - 1]);
                        imps = new ShaderProperty.Implementation[]
                        {
                            new ShaderProperty.Imp_VertexColor(shaderProperty)
                            {
                                Channels = channel.ToString().ToUpperInvariant()
                            }
                        };
                        break;
                    }
                }

                return imps;
            }
            
            static readonly GUIContent[] Presets = new[]
            {
                new GUIContent("Vertex Colors/R"),
                new GUIContent("Vertex Colors/G"),
                new GUIContent("Vertex Colors/B"),
                new GUIContent("Vertex Colors/A"),
                new GUIContent("Normal-Based/Local/X"),
                new GUIContent("Normal-Based/Local/Y"),
                new GUIContent("Normal-Based/Local/Z"),
                new GUIContent("Normal-Based/World/X"),
                new GUIContent("Normal-Based/World/Y"),
                new GUIContent("Normal-Based/World/Z"),
                new GUIContent("Position-Based/Local/X"),
                new GUIContent("Position-Based/Local/Y"),
                new GUIContent("Position-Based/Local/Z"),
                new GUIContent("Position-Based/World/X"),
                new GUIContent("Position-Based/World/Y"),
                new GUIContent("Position-Based/World/Z")
            };
        }
    }
}

// -----------------------------------------------------------------------------
// Merged from CodeInjection.cs
// -----------------------------------------------------------------------------

namespace ToonyColorsPro
{
	namespace ShaderGenerator
	{
		namespace CodeInjection
		{
			[Serialization.SerializeAs("codeInjection")]
			internal class CodeInjectionManager
			{
				[Serialization.SerializeAs("injectedPoint")]
				internal class InjectedPoint
				{
					// TODO instead of 'bool isReplace'
					internal enum ReplaceMode
					{
						Replace,
						Prepend,
						Append
					}

					[Serialization.SerializeAs("name")] internal string name;
					[Serialization.SerializeAs("enabled")] internal bool enabled = true;
					[Serialization.SerializeAs("replace")] internal bool isReplace;
					[Serialization.SerializeAs("replaceMode")] internal ReplaceMode replaceMode;
					[Serialization.SerializeAs("ignoreIndent")] internal bool ignoreIndent = true;
					[Serialization.SerializeAs("displayName")] internal string info;
					[Serialization.SerializeAs("blockName")] internal string blockName;
					[Serialization.SerializeAs("program")] internal ShaderProperty.ProgramType program;
					[Serialization.SerializeAs("shaderProperties")] internal List<ShaderProperty> shaderProperties = new List<ShaderProperty>();
					internal InjectableBlock block;

					// Contains the serialized properties as text, temporarily:
					// we need to parse the existing Shader Properties from the block first, and this is done when InjectedFile has been entirely Deserialized
					// then only we can unpack the temp serialized shader properties into the existing ones
					Dictionary<string, string> tempSerializedShaderProperties;

					// TODO On deserialization, compare matching shader properties and new ones, if any (if source file has changed)
					[Serialization.CustomDeserializeCallback]
					static InjectedPoint Deserialize(string strData, object[] args)
					{
						var ip = (InjectedPoint)Activator.CreateInstance(typeof(InjectedPoint), args);
						ip.tempSerializedShaderProperties = new Dictionary<string, string>();

						Func<object, string, object> onDeserializeShaderPropertyList = (obj, data) =>
						{
							//called with data in format 'list[sp(field:value;field:value...),sp(field:value;...)]'

							// - make a new list, and pull matching sp from it
							// - reset the implementations of the remaining sp for the undo/redo system
							// var shaderPropertiesTempList = new List<ShaderProperty>(ip.shaderProperties);

							var split = Serialization.SplitExcludingBlocks(data.Substring(5, data.Length - 6), ',', true, true, "()", "[]");
							foreach (var spData in split)
							{
								//try to match existing Shader Property by its name
								string name = null;

								//exclude 'sp(' and ')' and extract fields
								var vars = Serialization.SplitExcludingBlocks(spData.Substring(3, spData.Length - 4), ';', true, true, "()", "[]");
								foreach (var v in vars)
								{
									//find 'name' and remove 'name:' and quotes to extract value
									if (v.StartsWith("name:"))
									{
										name = v.Substring(6, v.Length - 7);
									}
								}

								if (name != null)
								{
									ip.tempSerializedShaderProperties.Add(name, spData);
								}
							}

							return null;
						};

						var shaderPropertyHandling = new Dictionary<Type, Func<object, string, object>> { { typeof(List<ShaderProperty>), onDeserializeShaderPropertyList } };

						return (InjectedPoint)Serialization.DeserializeTo(ip, strData, typeof(InjectedPoint), args, shaderPropertyHandling);
					}

					// Needed for serialization
					public InjectedPoint() { }
					
					public InjectedPoint(string name, ShaderProperty.ProgramType program, InjectableBlock block)
					{
						this.name = name;
						this.program = program;
						this.block = block;
						this.blockName = block.name;

						this.UpdateShaderProperties();
					}

					string GetShaderPropertyNameSuffix()
					{
						return "_" + block.name.GetHashCode();
					}

					internal void UpdateShaderProperties()
					{
						foreach (var spi in block.shaderPropertiesInfos)
						{
							string spName = string.Format("{0}{1}", spi.name, GetShaderPropertyNameSuffix());
							if (!shaderProperties.Exists(sp => sp.Name == spName))
							{
								var sp = new ShaderProperty(spName, spi.variableType);
								sp.DisplayName = spi.name;
								sp.Program = this.isReplace ? spi.programType : this.program;
								sp.deferredSampling = true;

								var imp_constant = (sp.implementations[0] as ShaderProperty.Imp_ConstantValue);
								imp_constant.Label = spi.name;
								if (imp_constant != null && spi.defaultValue != null)
								{
									switch (spi.variableType)
									{
										case ShaderProperty.VariableType.@float:
										{
											float value;
											if (float.TryParse(spi.defaultValue, out value))
											{
												imp_constant.FloatValue = value;
											}
										}
										break;

										case ShaderProperty.VariableType.float2:
										{
											Vector2 value = Vector2.zero;
											var array = ExtractDefaultValue(spi.defaultValue);
											if (array.Length >= 1) value.x = array[0];
											if (array.Length >= 2) value.y = array[1];
											imp_constant.Float2Value = value;
										}
										break;

										case ShaderProperty.VariableType.float3:
										{
											Vector3 value = Vector3.zero;
											var array = ExtractDefaultValue(spi.defaultValue);
											if (array.Length >= 1) value.x = array[0];
											if (array.Length >= 2) value.y = array[1];
											if (array.Length >= 3) value.z = array[2];
											imp_constant.Float3Value = value;
										}
										break;

										case ShaderProperty.VariableType.float4:
										{
											Vector4 value = Vector4.zero;
											var array = ExtractDefaultValue(spi.defaultValue);
											if (array.Length >= 1) value.x = array[0];
											if (array.Length >= 2) value.y = array[1];
											if (array.Length >= 3) value.z = array[2];
											if (array.Length >= 4) value.w = array[3];
											imp_constant.Float4Value = value;
										}
										break;

										case ShaderProperty.VariableType.color:
										case ShaderProperty.VariableType.color_rgba:
										{
											Color value = new Color();
											var array = ExtractDefaultValue(spi.defaultValue);
											if (array.Length >= 1) value.r = array[0];
											if (array.Length >= 2) value.g = array[1];
											if (array.Length >= 3) value.b = array[2];
											if (array.Length >= 4) value.a = array[3]; else value.a = 1.0f;
											imp_constant.ColorValue = value;
										}
										break;
									}
								}

								sp.SetDefaultImplementations(sp.implementations.ToArray());
								sp.ForceUpdateDefaultHash();

								// TODO RESTRICT IMPLEMENTATIONS USABLE BY THIS SHADER PROPERTY

								shaderProperties.Add(sp);
							}
						}

						// Unpack serialized data into the shader properties
						if (tempSerializedShaderProperties != null)
						{
							foreach (var sp in shaderProperties)
							{
								if (tempSerializedShaderProperties.ContainsKey(sp.Name))
								{
									Func<object, string, object> onDeserializeImplementation = (impObj, impData) =>
									{
										return ShaderGenerator2.CurrentConfig.DeserializeImplementationHandler(impObj, impData, sp);
									};
									var implementationHandling = new Dictionary<Type, Func<object, string, object>> { { typeof(ShaderProperty.Implementation), onDeserializeImplementation } };

									string serializedData = tempSerializedShaderProperties[sp.Name];
									Serialization.DeserializeTo(sp, serializedData, typeof(ShaderProperty), null, implementationHandling);
								}
							}

							foreach (var sp in shaderProperties)
							{
								sp.CheckErrors();
								sp.CheckHash();
							}
						}
					}

					internal void InjectCode(StringBuilder stringBuilder, string indent)
					{
						var newLines = GetCodeLinesWithReplacedVariables(indent);
						foreach (string line in newLines)
						{
							stringBuilder.AppendLine(line);
						}
					}

					internal List<string> GetCodeLinesWithReplacedVariables(string indent, bool ignoreLineIndent = false)
					{
						var newLinesList = new List<string>();
						foreach (var line in block.codeLines)
						{
							string newLine = null;
							foreach (var sp in shaderProperties)
							{
								string variableName = sp.Name.Substring(0, sp.Name.Length - GetShaderPropertyNameSuffix().Length);
								string pattern = string.Format("\\b{0}\\b", variableName);
								if (Regex.IsMatch(line, pattern))
								{
									// figure out indent from current line to properly align variable declaration
									string lineIndent = "";
									if (!ignoreLineIndent)
									{
										for (int i = 0; i < line.Length; i++)
										{
											if (char.IsWhiteSpace(line[i]))
											{
												lineIndent += line[i];
											}
											else
											{
												break;
											}
										}
									}

									// append variable declaration
									newLinesList.Add(indent + lineIndent + sp.PrintVariableSampleDeferred(ShaderGenerator2.CurrentInput, ShaderGenerator2.CurrentOutput, ShaderGenerator2.CurrentProgram, null, true));

									// replace variable name with declared variable name from shader property
									newLine = Regex.Replace(line, pattern, sp.GetVariableName());
								}
							}
							
							newLinesList.Add(indent + (newLine ?? (ignoreLineIndent ? line.TrimStart() : line)));
						}
						
						return newLinesList;
					}
				}

				internal class InjectableBlock
				{
					internal string name;
					internal string[] codeLines;
					internal List<ShaderPropertyInfo> shaderPropertiesInfos = new List<ShaderPropertyInfo>();

					internal bool isReplaceBlock;
					internal InjectedPoint.ReplaceMode replaceMode;
					internal string searchString;
					internal string info;
					internal string autoInjection;

					internal bool IsSameAs(InjectableBlock otherBlock)
					{
						if (!this.isReplaceBlock)
						{
							bool same = !otherBlock.isReplaceBlock && this.name == otherBlock.name && this.autoInjection == otherBlock.autoInjection && this.shaderPropertiesInfos.Count == otherBlock.shaderPropertiesInfos.Count;
							if (!same)
							{
								return false;
							}
							
							// verify shader properties, in case they have changed
							for (int i = 0; i < shaderPropertiesInfos.Count; i++)
							{
								if (!shaderPropertiesInfos[i].IsSameAs(otherBlock.shaderPropertiesInfos[i]))
								{
									return false;
								}
							}

							return true;
						}
						else
						{
							return otherBlock.isReplaceBlock && this.name == otherBlock.name && otherBlock.searchString == this.searchString && otherBlock.info == this.info;
						}
					}
				}

				internal class ShaderPropertyInfo
				{
					internal string name;
					internal string defaultValue;

					internal ShaderProperty.ProgramType programType = ShaderProperty.ProgramType.Undefined; // ==> should be determined by where the injection point is hooked
					internal ShaderProperty.VariableType variableType = ShaderProperty.VariableType.@float;
					ShaderProperty.ColorPrecision colorPrecision = ShaderProperty.ColorPrecision.LDR;
					ShaderProperty.FloatPrecision floatPrecision = ShaderProperty.FloatPrecision.@float;

					internal bool IsSameAs(ShaderPropertyInfo other)
					{
						return this.programType == other.programType
						       && this.variableType == other.variableType
						       && this.colorPrecision == other.colorPrecision
						       && this.floatPrecision == other.floatPrecision
						       && this.name == other.name
						       && this.defaultValue == other.defaultValue;
					}
				}

				// Select a file to inject blocks from, to one or more injection points
				[Serialization.SerializeAs("injectedFile")]
				internal class InjectedFile
				{
					internal TextAsset includeFile;
					[Serialization.SerializeAs("guid")] string guid;
					[Serialization.SerializeAs("filename")] string filename;
					int contentHash;

					[Serialization.SerializeAs("injectedPoints")] internal List<InjectedPoint> injectedPoints = new List<InjectedPoint>();
					internal List<InjectableBlock> injectableBlocks = new List<InjectableBlock>();
					int replaceBlockCount;

					// UI
					Dictionary<InjectedPoint, bool> headersExpanded = new Dictionary<InjectedPoint, bool>();
					GenericMenu pendingBlockMenu;
					ReorderableLayoutList injectedPointsList = new ReorderableLayoutList();

					public InjectedFile()
					{
						ShaderGenerator2.onProjectChange += onProjectChanged;
					}

					internal void WillBeRemoved()
					{
						ShaderGenerator2.onProjectChange -= onProjectChanged;
					}

					void onProjectChanged()
					{
						VerifyCodeInjectionFile();
					}

					[Serialization.OnDeserializeCallback]
					void OnDeserialize()
					{
						// Find back includeFile from guid
						if (!string.IsNullOrEmpty(guid))
						{
							string path = AssetDatabase.GUIDToAssetPath(guid);
							if (string.IsNullOrEmpty(path))
							{
								Debug.LogError("[SG2 Code Injection] Can't find path for Code Injection file GUID: " + guid + " (filename: \"" + filename + "\")");
								return;
							}

							var file = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
							TryParseIncludeFile(file, null);
						}

						// Link back blocks to injected points and update headers array
						for (int i = injectedPoints.Count - 1; i >= 0; i--)
						{
							var ip = injectedPoints[i];

							if (string.IsNullOrEmpty(ip.blockName))
							{
								Debug.LogWarning("[SG2 Code Injection] Block name was not properly serialized.");
								injectedPoints.RemoveAt(i);
								continue;
							}

							var matchingBlock = this.injectableBlocks.Find(block => block.name == ip.blockName);
							if (matchingBlock == null)
							{
								Debug.LogWarning(string.Format("[SG2 Code Injection] Block wasn't found in source file. Block name: \"{0}\", Source file: \"{1}\"", ip.blockName, this.filename));
								injectedPoints.RemoveAt(i);
								continue;
							}

							ip.block = matchingBlock;
							ip.UpdateShaderProperties();
							headersExpanded[ip] = false;
						}
						
						VerifyCodeInjectionFile();
					}

					void VerifyCodeInjectionFile()
					{
						if (!string.IsNullOrEmpty(guid))
						{
							string path = AssetDatabase.GUIDToAssetPath(guid);
							string fileContent = File.ReadAllText(Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length) + path);
							int fileHash = fileContent.GetHashCode();
							
							if (fileHash == contentHash)
							{
								// same hash, no need to verify
								return;
							}
							contentHash = fileHash;
							
							var blockList = TryParseFileForInjectableBlocks(fileContent);

							// Iterate existing blocks and see if they still exist in the file:
							for (int i = injectableBlocks.Count - 1; i >= 0; i--)
							{
								var existingBlock = injectableBlocks[i];
								
								if (!blockList.Exists(b => b.IsSameAs(existingBlock)))
								{
									// Block doesn't exist anymore in the source file: remove it
									if (existingBlock.isReplaceBlock)
									{
										replaceBlockCount--;
										RemoveReplaceBlock(existingBlock);
									}
									injectableBlocks.RemoveAt(i);

									for (int j = injectedPoints.Count - 1; j >= 0; j--)
									{
										if (injectedPoints[j].blockName == existingBlock.name)
										{
											RemoveInjectedPoint(j);
										}
									}
								}
							}
							
							foreach (var fileBlock in blockList)
							{
								// Block from file is new: add it
								if (!injectableBlocks.Exists(b => b.IsSameAs(fileBlock)))
								{
									injectableBlocks.Add(fileBlock);
									if (fileBlock.isReplaceBlock)
									{
										replaceBlockCount++;
										AddReplaceBlock(fileBlock);
									}
								}
								
								// Auto-injected block that isn't injected yet
								if (fileBlock.autoInjection != null && !injectedPoints.Exists(item => item.blockName == fileBlock.name))
								{
									var ip = ShaderGenerator2.CurrentTemplate.injectionPoints.Find(item => item.name == fileBlock.autoInjection);
									if (ip != null)
									{
										AddBlockAtInjectionPoint(ip, fileBlock);
									}
								}
							}

							foreach (var injectedPoint in injectedPoints)
							{
								injectedPoint.UpdateShaderProperties();
							}
						}
					}

					bool TryParseIncludeFile(TextAsset file, Template template)
					{
						// template == null means we're doing that after deserialization
						if (template != null)
						{
							injectedPoints.Clear();
						}

						injectableBlocks.Clear();
						headersExpanded.Clear();
						replaceBlockCount = 0;

						if (file == null)
						{
							includeFile = null;
							guid = null;
							filename = null;
							return true;
						}

						string fileContent = File.ReadAllText(Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length) + AssetDatabase.GetAssetPath(file));
						if (string.IsNullOrEmpty(fileContent))
						{
							return false;
						}

						contentHash = fileContent.GetHashCode();

						var fileBlocks = TryParseFileForInjectableBlocks(fileContent);
						if (fileBlocks == null)
						{
							return false;
						}

						foreach (var block in fileBlocks)
						{
							injectableBlocks.Add(block);
							if (block.isReplaceBlock)
							{
								replaceBlockCount++;
								AddReplaceBlock(block);
							}
						}

						includeFile = file;
						filename = includeFile.name;
						guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(includeFile));

						// Parse auto-inject points and add them if found
						if (template != null)
						{
							foreach (var block in fileBlocks)
							{
								if (block.autoInjection != null)
								{
									foreach (var injectionPoint in template.injectionPoints)
									{
										if (injectionPoint.name == block.autoInjection)
										{
											AddBlockAtInjectionPoint(injectionPoint, block);
										}
									}
								}
							}
						}

						return true;
					}

					List<InjectableBlock> TryParseFileForInjectableBlocks(string fileContent)
					{
						var blockList = new List<InjectableBlock>();
						
						using (var stringReader = new StringReader(fileContent))
						{
							int lineNb = 0;
							string line;
							try
							{
								InjectableBlock currentBlock = null;
								var codeLines = new List<string>();

								void AddCurrentBlock()
								{
									if (currentBlock != null)
									{
										int i = codeLines.Count - 1;
										while (i >= 0 && codeLines[i] == "")
										{
											codeLines.RemoveAt(codeLines.Count - 1);
											i--;
										}

										if (codeLines.Count > 0 && ((currentBlock.isReplaceBlock && currentBlock.searchString != "") || !currentBlock.isReplaceBlock))
										{
											currentBlock.codeLines = codeLines.ToArray();
											blockList.Add(currentBlock);
										}

										codeLines.Clear();
									}
								}

								bool parsingSearchString = false;
								while ((line = stringReader.ReadLine()) != null)
								{
									string trimmedLine = line.Trim();
									string trimmedLineLower = trimmedLine.ToLowerInvariant();

									if (line.StartsWith("///"))
									{
										continue;
									}

									if (line.StartsWith("//# "))
									{
										// new block
										if (trimmedLine.StartsWith("//# BLOCK:"))
										{
											AddCurrentBlock();

											string blockName = trimmedLine.Substring("//# BLOCK:".Length).Trim();
											if (string.IsNullOrEmpty(blockName))
											{
												throw new System.Exception("Line '//# BLOCK:' requires a name, please see the documentation");
											}

											currentBlock = new InjectableBlock()
											{
												name = blockName
											};
										}
										// new replace block
										else if (trimmedLine.StartsWith("//# REPLACE:"))
										{
											AddCurrentBlock();

											string blockName = trimmedLine.Substring("//# REPLACE:".Length).Trim();
											if (string.IsNullOrEmpty(blockName))
												throw new System.Exception("Line '//# REPLACE:' requires a name, please see the documentation");

											currentBlock = new InjectableBlock()
											{
												name = blockName,
												isReplaceBlock = true,
												replaceMode = InjectedPoint.ReplaceMode.Replace,
												searchString = ""
											};
											parsingSearchString = true;
										}
										// new append block
										else if (trimmedLine.StartsWith("//# APPEND:"))
										{
											AddCurrentBlock();

											string blockName = trimmedLine.Substring("//# APPEND:".Length).Trim();
											if (string.IsNullOrEmpty(blockName))
												throw new System.Exception("Line '//# APPEND:' requires a name, please see the documentation");

											currentBlock = new InjectableBlock()
											{
												name = blockName,
												isReplaceBlock = true,
												replaceMode = InjectedPoint.ReplaceMode.Append,
												searchString = ""
											};
											parsingSearchString = true;
										}
										// new prepend block
										else if (trimmedLine.StartsWith("//# PREPEND:"))
										{
											AddCurrentBlock();

											string blockName = trimmedLine.Substring("//# PREPEND:".Length).Trim();
											if (string.IsNullOrEmpty(blockName))
												throw new System.Exception("Line '//# PREPEND:' requires a name, please see the documentation");

											currentBlock = new InjectableBlock()
											{
												name = blockName,
												isReplaceBlock = true,
												replaceMode = InjectedPoint.ReplaceMode.Prepend,
												searchString = ""
											};
											parsingSearchString = true;
										}
										else if (trimmedLine.StartsWith("//# WITH:"))
										{
											if (currentBlock == null)
											{
												throw new System.Exception("'WITH:' tag outside of block");
											}
											if (!currentBlock.isReplaceBlock)
											{
												throw new System.Exception("'WITH:' tag only works with 'REPLACE:' blocks");
											}

											// replace block replacement
											parsingSearchString = false;
										}
										else if (trimmedLineLower.StartsWith("//# inject @"))
										{
											if (currentBlock == null)
											{
												throw new System.Exception("'Inject @' tag outside of block");
											}

											string autoInjectPoint = trimmedLine.Substring("//# inject @".Length).Trim();

											currentBlock.autoInjection = autoInjectPoint;
										}
										else if (trimmedLineLower.StartsWith("//# info:"))
										{
											if (currentBlock == null)
											{
												throw new System.Exception("'INFO:' tag outside of block");
											}
											if (!currentBlock.isReplaceBlock)
											{
												throw new System.Exception("'INFO:' tag only works with 'REPLACE:' blocks");
											}

											currentBlock.info = trimmedLine.Substring("//# info:".Length).Trim();
										}
										// variable to parse
										else if (trimmedLineLower.StartsWith("//# float") || (trimmedLineLower.StartsWith("//# fragment") || trimmedLineLower.StartsWith("//# vertex")))
										{
											// Prevent Shader Properties for Replace blocks, it's more complicated than I initially thought to implement...
											if (currentBlock.isReplaceBlock)
											{
												continue;
											}

											if (currentBlock == null)
											{
												throw new System.Exception("Property declaration outside of block");
											}

											if (currentBlock.isReplaceBlock && trimmedLineLower.StartsWith("//# float"))
											{
												throw new System.Exception("//# REPLACE block variables must declare their shader program first ('vertex' or 'fragment')");
											}

											if (!currentBlock.isReplaceBlock && !trimmedLineLower.StartsWith("//# float"))
											{
												throw new System.Exception("Regular block variables must not declare the shader program");
											}

											string[] parts = trimmedLine.Split(new char[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
											int startIndex = currentBlock.isReplaceBlock ? 1 : 0;

											ShaderProperty.VariableType variableType;
											switch (parts[startIndex + 1])
											{
												case "float": variableType = ShaderProperty.VariableType.@float; break;
												case "float2": variableType = ShaderProperty.VariableType.float2; break;
												case "float3": variableType = ShaderProperty.VariableType.float3; break;
												case "float4": variableType = ShaderProperty.VariableType.float4; break;
												case "color": variableType = ShaderProperty.VariableType.color_rgba; break;
												case "color_rgba": variableType = ShaderProperty.VariableType.color_rgba; break;
												default: throw new System.Exception("Invalid parsed property type: " + parts[1]);
											}
											string name = parts[startIndex + 2];
											string defaultValue = (parts.Length >= (startIndex + 4)) ? parts[startIndex + 3] : null;

											// check if property already exists with this block
											foreach (var existingSpi in currentBlock.shaderPropertiesInfos)
											{
												if (existingSpi.name == name)
												{
													throw new System.Exception("A property already exists with the same name: " + name);
												}
											}

											var spi = new ShaderPropertyInfo()
											{
												name = name,
												variableType = variableType,
												defaultValue = defaultValue
											};

											if (currentBlock.isReplaceBlock)
											{
												ShaderProperty.ProgramType programType = ShaderProperty.ProgramType.Fragment;
												if (parts[0].ToLowerInvariant() == "vertex")
												{
													programType = ShaderProperty.ProgramType.Vertex;
												}
												spi.programType = programType;
											}

											currentBlock.shaderPropertiesInfos.Add(spi);
										}
									}
									else if (currentBlock != null)
									{
										if (currentBlock.isReplaceBlock && parsingSearchString)
										{
											currentBlock.searchString += currentBlock.searchString == "" ? line : Environment.NewLine + line;
										}
										else
										{
											codeLines.Add(line);
										}
									}

									lineNb++;
								}
								AddCurrentBlock();
							}
							catch (System.Exception e)
							{
								Debug.LogError(string.Format("[SG2 Code Injection] Couldn't load code injection include file, error at line {0}:  {1}", lineNb, e.ToString()));
								return null;
							}
						}

						return blockList;
					}

					internal void ShowGUI(Template template, float margin)
					{
						if (pendingBlockMenu != null)
						{
							pendingBlockMenu.ShowAsContext();
							pendingBlockMenu = null;
						}


						// Include file

						TextAsset newIncludeFile = includeFile;
						System.Action parseNewFile = () =>
						{
							if (newIncludeFile != includeFile)
							{
								if (!TryParseIncludeFile(newIncludeFile, template))
								{
									includeFile = null;
									Debug.LogError(ShaderGenerator2.ErrorMsg("[SG2 Code Injection] Couldn't load code injection include file."));
								}
							}
						};

						if (includeFile == null)
						{
							GUILayout.BeginHorizontal();
							{
								GUILayout.Space(margin);
								newIncludeFile = (TextAsset)EditorGUILayout.ObjectField(TCP2_GUI.TempContent("Source File", "Select a source file from which to insert custom code, with the .cginc or .hlslinc format"), includeFile, typeof(TextAsset), false);
							}
							GUILayout.EndHorizontal();

							GUILayout.BeginHorizontal();
							{
								GUILayout.Space(margin);
								EditorGUILayout.HelpBox("Please select a valid include file with the correct formatting adapted for Code Injection.\nSee the documentation for more information!", MessageType.Info);
							}
							GUILayout.EndHorizontal();
							parseNewFile();
							return;
						}

						GUILayout.BeginHorizontal();
						{
							GUILayout.Space(margin);
							newIncludeFile = (TextAsset) EditorGUILayout.ObjectField(TCP2_GUI.TempContent("Source File", "Select a source file from which to insert custom code, with the .cginc or .hlslinc format"), includeFile, typeof(TextAsset), false);
						}
						GUILayout.EndHorizontal();
						
						GUILayout.BeginHorizontal();
						{
							GUILayout.Space(margin);
							if (GUILayout.Button("Add Block at Injection Point", GUILayout.ExpandWidth(false)))
							{
								var injectionPointMenu = new GenericMenu();
								injectionPointMenu.AddDisabledItem(new GUIContent("Select an injection point:"));
								injectionPointMenu.AddSeparator("");

								foreach (var ip in template.injectionPoints)
								{
									injectionPointMenu.AddItem(new GUIContent(ip.name), false, OnAddInjectionPoint, ip);
								}

								if (template.injectionPoints.Count == 0)
								{
									injectionPointMenu.AddDisabledItem(new GUIContent("No injection points were found in this template!"));
								}

								if (injectableBlocks.Count == 0)
								{
									injectionPointMenu.AddDisabledItem(new GUIContent("No injectable blocks were found in the selected file!"));
								}

								injectionPointMenu.ShowAsContext();
							}
						}
						GUILayout.EndHorizontal();

						int injectedPointToRemove = -1;

						// List of added blocks/injection point
						Action<int, float> drawInjectedPoint = (index, margin2) =>
						{
							GUILayout.BeginHorizontal();
							{
								GUILayout.Space(margin2);
								TCP2_GUI.SeparatorSimple();
							}
							GUILayout.EndHorizontal();

							var point = injectedPoints[index];

							Rect removeButtonRect;
							Rect enableButtonRect;
							
							bool guiEnabled = GUI.enabled;
							GUI.enabled &= point.enabled;
							
							EditorGUILayout.BeginHorizontal();
							{
								GUILayout.Space(margin + margin2);

								string prefix = !point.isReplace ? "@ "
									: point.replaceMode == InjectedPoint.ReplaceMode.Append ? "Append block ("
										: point.replaceMode == InjectedPoint.ReplaceMode.Prepend ? "Prepend block ("
											: "Replace block (";
								GUIContent label = TCP2_GUI.TempContent(prefix + point.name);
								Rect rect = GUILayoutUtility.GetRect(label, EditorStyles.label, GUILayout.ExpandWidth(true));
								rect.xMin += 4; // small left padding

								enableButtonRect = rect;
								enableButtonRect.width = 20;
								rect.xMin += enableButtonRect.width;

								removeButtonRect = rect;
								removeButtonRect.width = 22;
								removeButtonRect.height = 22;
								removeButtonRect.y -= 22 - rect.height;
								removeButtonRect.x += rect.width;

								GUI.Label(rect, label, EditorStyles.label);

								// Ignore indent toggle (replace blocks)
								if (point.isReplace)
								{
									float labelWidth = EditorStyles.label.CalcSize(label).x;
									Rect toggleRect = rect;
									toggleRect.x += labelWidth + 2;
									toggleRect.width = 118;
									point.ignoreIndent = GUI.Toggle(toggleRect, point.ignoreIndent, "Ignore indent spaces", miniToggle);

									toggleRect.x = toggleRect.xMax + 2;
									toggleRect.width = 10;
									GUI.Label(toggleRect, ")");
								}

								GUILayout.Space(removeButtonRect.width);
							}
							EditorGUILayout.EndHorizontal();

							margin2 += enableButtonRect.width;

							EditorGUILayout.BeginHorizontal();
							{
								GUILayout.Space(margin + margin2);

								EditorGUI.BeginChangeCheck();
								{
									// hover rect as in 2019.3 UI
									var label = TCP2_GUI.TempContent(point.block.name);
									var rect = GUILayoutUtility.GetRect(label, EditorStyles.foldout, GUILayout.ExpandWidth(true));
									Rect hoverRect = rect;
									rect.xMin += 4; // small left padding

									// removeButtonRect.yMax = rect.yMax;
									// rect.xMax -= removeButtonRect.width;

									bool hasShaderProperties = point.shaderProperties.Count > 0;
									if (hasShaderProperties)
									{
										TCP2_GUI.DrawHoverRect(hoverRect);
										bool highlight = point.shaderProperties.Exists(sp => sp.manuallyModified);
										headersExpanded[point] = TCP2_GUI.HeaderFoldoutHighlightErrorGrayPosition(rect, headersExpanded[point], label, false, highlight);
									}
									else
									{
										GUI.Label(rect, label, EditorStyles.boldLabel);
									}
								}
								if (EditorGUI.EndChangeCheck())
								{
									// expand/fold all when alt/control is held
									/*
									if (Event.current.alt || Event.current.control)
									{
										if (headersExpanded[group.header.text])
										{
											ExpandAllGroups();
										}
										else
										{
											FoldAllGroups();
										}
									}
									*/
								}
								
								GUILayout.Space(removeButtonRect.width);
							}
							EditorGUILayout.EndHorizontal();

							if (point.isReplace && !string.IsNullOrEmpty(point.block.info))
							{
								EditorGUILayout.BeginHorizontal();
								{
									GUILayout.Space(margin + margin2 + 4);
									GUILayout.Label(TCP2_GUI.TempContent(point.block.info), EditorStyles.wordWrappedMiniLabel);
									GUILayout.Space(removeButtonRect.width);
								}
								EditorGUILayout.EndHorizontal();
							}

							GUI.enabled = guiEnabled;

							// Enable button
							Rect lastRect = GUILayoutUtility.GetLastRect();
							enableButtonRect.y = (lastRect.y + enableButtonRect.y) / 2.0f;
							point.enabled = GUI.Toggle(enableButtonRect, point.enabled, GUIContent.none);

							// Remove button
							removeButtonRect.y = (lastRect.y + removeButtonRect.y) / 2.0f;
							if (!point.isReplace && GUI.Button(removeButtonRect, "X"))
							{
								injectedPointToRemove = index;
							}

							if (headersExpanded[point])
							{
								foreach (var sp in point.shaderProperties)
								{
									sp.ShowGUILayout(margin + margin2 + 8);
								}
							}
						};

						GUILayout.Space(4);

						// List of injected blocks
						RectOffset injectedPointListPadding = new RectOffset((int)margin, 0, 0, 0);
						injectedPointsList.DoLayoutList(drawInjectedPoint, injectedPoints, injectedPointListPadding);

						if (injectedPointToRemove >= 0)
						{
							RemoveInjectedPoint(injectedPointToRemove);
						}

						GUILayout.Space(2);

						parseNewFile();
					}


					void RemoveInjectedPoint(int index)
					{
						var ip = injectedPoints[index];
						headersExpanded.Remove(ip);
						injectedPoints.RemoveAt(index);
					}

					void OnAddInjectionPoint(object ip)
					{
						var injectionPoint = (Template.InjectionPoint)ip;
						var blocksMenu = new GenericMenu();
						blocksMenu.AddDisabledItem(new GUIContent("Select a code block to inject:"));
						blocksMenu.AddSeparator("");

						foreach (var block in injectableBlocks)
						{
							if (block.isReplaceBlock) continue;

							if (this.injectedPoints.Exists(item => item.block == block))
							{
								blocksMenu.AddDisabledItem(new GUIContent(block.name + " (already added)"));
							}
							else
							{
								blocksMenu.AddItem(new GUIContent(block.name), false, OnAddBlock, new object[] { injectionPoint, block });
							}
						}

						pendingBlockMenu = blocksMenu;
					}

					void OnAddBlock(object data)
					{
						var array = (object[])data;
						var injectionPoint = (Template.InjectionPoint)array[0];
						var block = (InjectableBlock)array[1];

						AddBlockAtInjectionPoint(injectionPoint, block);
					}

					void AddBlockAtInjectionPoint(Template.InjectionPoint injectionPoint, InjectableBlock block)
					{
						var ip = new InjectedPoint(injectionPoint.name, injectionPoint.program, block);
						injectedPoints.Add(ip);
						headersExpanded.Add(ip, true);
					}

					void AddReplaceBlock(InjectableBlock block)
					{
						if (!block.isReplaceBlock)
						{
							return;
						}

						if (injectedPoints.Exists(i => i.blockName == block.name))
						{
							return;
						}

						var ip = new InjectedPoint()
						{
							isReplace = true,
							replaceMode = block.replaceMode,
							block = block,
							blockName = block.name,
							info = block.info
						};
						injectedPoints.Add(ip);
						ip.UpdateShaderProperties();
						headersExpanded.Add(ip, true);
					}

					void RemoveReplaceBlock(InjectableBlock block)
					{
						var foundIp = injectedPoints.Find(ip => ip.blockName == block.name);
						injectedPoints.Remove(foundIp);
					}

					static GUIStyle _miniToggle;
					static GUIStyle miniToggle
					{
						get
						{
							if(_miniToggle == null)
							{
								_miniToggle = "ShurikenToggle";
								_miniToggle.fontSize = 10;
								var color = _miniToggle.normal.textColor;
								_miniToggle.hover.textColor = color;
								_miniToggle.onHover.textColor = color;
								_miniToggle.onNormal.textColor = color;
								_miniToggle.active.textColor = color;
								_miniToggle.onActive.textColor = color;
							}
							return _miniToggle;
						}
					}
				}

				//================================================================================================================================

				internal static CodeInjectionManager instance;

				[Serialization.SerializeAs("injectedFiles")] internal List<InjectedFile> injectedFiles = new List<InjectedFile>();
				[Serialization.SerializeAs("mark")] bool markInjectionPoints = false;

				ReorderableLayoutList injectedFilesList = new ReorderableLayoutList();

				public CodeInjectionManager()
				{
					instance = this;
				}

				internal void ShowGUI(Template template)
				{
					markInjectionPoints = EditorGUILayout.Toggle(TCP2_GUI.TempContent("Mark injection points", "Add a comment for each injection point in the output file, to easily identify their locations, e.g.\n\"// Injection Point: Properties/Start\""), markInjectionPoints);

					// Info
					if (this.injectedFiles.Count == 0)
					{
						EditorGUILayout.HelpBox("No injected file added.", MessageType.Info);
					}

					// Draw list
					int injectedFileToRemove = -1;
					Action<int, float> drawInjectedFile = (index, margin) =>
					{
						EditorGUILayout.BeginVertical(EditorStyles.helpBox);
						{
							GUILayout.BeginHorizontal();
							{
								GUILayout.Space(margin);
								GUILayout.Label("Injected File", EditorStyles.boldLabel);
								GUILayout.FlexibleSpace();
								if (GUILayout.Button(TCP2_GUI.TempContent("-", "Remove this injected file")))
								{
									injectedFileToRemove = index;
								}
							}
							GUILayout.EndHorizontal();
							injectedFiles[index].ShowGUI(template, margin);
						}
						EditorGUILayout.EndVertical();
					};
					injectedFilesList.DoLayoutList(drawInjectedFile, injectedFiles, 10);

					if (injectedFileToRemove >= 0)
					{
						this.injectedFiles[injectedFileToRemove].WillBeRemoved();
						this.injectedFiles.RemoveAt(injectedFileToRemove);
					}

					// Add button
					GUILayout.BeginHorizontal();
					{
						GUILayout.FlexibleSpace();
						if (GUILayout.Button("Add Injected File", GUILayout.ExpandWidth(false), GUILayout.Height(30)))
						{
							injectedFiles.Add(new InjectedFile());
						}
					}
					GUILayout.EndHorizontal();
				}

				internal string[] GetNeededFeatures()
				{
					List<string> list = new List<string>();
					foreach (var file in injectedFiles)
					{
						foreach (var point in file.injectedPoints)
						{
							if (!point.enabled)
							{
								continue;
							}
							
							foreach (var sp in point.shaderProperties)
							{
								list.AddRange(sp.NeededFeatures());
							}
						}
					}
					return list.ToArray();
				}

				internal string GetCodeForInjectionPoint(string injectionPoint, string indent)
				{
					var sb = new StringBuilder();

					sb.AppendLine(string.Format("{0}//================================", indent));
					sb.AppendLine(string.Format("{0}// Injected Code for '{1}'", indent, injectionPoint));

					bool hasCode = false;
					foreach (var file in injectedFiles)
					{
						foreach (var point in file.injectedPoints)
						{
							if (!point.enabled)
							{
								continue;
							}
							
							if (point.name == injectionPoint)
							{
								hasCode = true;
								point.InjectCode(sb, indent);
							}
						}
					}

					if (!hasCode)
					{
						return markInjectionPoints ? string.Format("{0}// Injection Point: '{1}'", indent, injectionPoint) : "";
					}

					sb.AppendLine(string.Format("{0}//================================", indent));
					return sb.ToString();
				}

				internal void ProcessReplaceBlocks(StringBuilder stringBuilder)
				{
					foreach (var file in injectedFiles)
					{
						foreach (var ip in file.injectedPoints)
						{
							if (!ip.enabled)
							{
								continue;
							}
							
							if (ip.block.isReplaceBlock)
							{
								bool isAppend = ip.block.replaceMode == InjectedPoint.ReplaceMode.Append;
								bool isPrepend = ip.block.replaceMode == InjectedPoint.ReplaceMode.Prepend;

								if (ip.ignoreIndent)
								{
									var sourceLines = new List<string>(stringBuilder.ToString().Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
									string[] linesToSearch = ip.block.searchString.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
									if (linesToSearch.Length == 0)
										continue;

									int startIndex = 0;
									int overflow = 0;
									REPEAT_LOOP:
									for (int i = startIndex; i < sourceLines.Count; i++)
									{
										overflow++;
										if (overflow > 99999)
											throw new OverflowException();

										string line = sourceLines[i];
										string trimmedLine = line.TrimStart();
										string minIndent = trimmedLine.Length > 0 ? line.Replace(trimmedLine, "") : "";
										if (trimmedLine == linesToSearch[0].TrimStart())
										{
											int matchIndex = i;
											bool matchAllLines = true;

											for (int j = 1; j < linesToSearch.Length; j++)
											{
												string searchLine = linesToSearch[j];
												string trimmedSearchLine = searchLine.TrimStart();
												string trimmedSourceLine = sourceLines[i + j].TrimStart();
												if (trimmedSourceLine != trimmedSearchLine)
												{
													matchAllLines = false;
													break;
												}

												string indent = trimmedSourceLine.Length > 0 ? sourceLines[i + j].Replace(trimmedSourceLine, "") : "";
												if (indent.Length < minIndent.Length)
													minIndent = indent;
											}

											if (matchAllLines)
											{
												if (!isAppend && !isPrepend)
												{
													for (int j = 0; j < linesToSearch.Length; j++)
														sourceLines.RemoveAt(matchIndex);
												}

												var replaceList = ip.GetCodeLinesWithReplacedVariables(minIndent, true);
												replaceList.Insert(0, "//================================");
												replaceList.Insert(1, $"// {(isAppend ? "Appended" : isPrepend ? "Prepended" : "Replaced")} through Code Injection:");
												replaceList.Add("//================================");

												for (int j = replaceList.Count - 1; j >= 0; j--)
												{
													string replacement = replaceList[j];
													if (!isAppend)
														sourceLines.Insert(matchIndex, replacement);
													else
														sourceLines.Insert(matchIndex + linesToSearch.Length, replacement);
												}

												startIndex = matchIndex + replaceList.Count + 1;
												goto REPEAT_LOOP;
											}
										}
									}

									stringBuilder.Clear();
									foreach (string line in sourceLines)
										stringBuilder.AppendLine(line);
								}
								else
								{
									var replaceList = ip.GetCodeLinesWithReplacedVariables("");
									replaceList.Insert(0, "//================================");
									replaceList.Insert(1, "// Replaced through Code Injection:");
									replaceList.Add("//================================");
									string replaceLines = string.Join(Environment.NewLine, replaceList);

									if (isAppend)
										replaceLines += $"{Environment.NewLine}{ip.block.searchString}";
									if (isPrepend)
										replaceLines = $"{ip.block.searchString}{Environment.NewLine}{replaceLines}";

									stringBuilder = stringBuilder.Replace(ip.block.searchString, replaceLines);
								}
							}
						}
					}
				}

				internal List<ShaderProperty> GetShaderPropertiesForInjectionPoint(string injectionPoint)
				{
					var list = new List<ShaderProperty>();

					foreach (var file in injectedFiles)
					{
						foreach (var point in file.injectedPoints)
						{
							if (!point.enabled)
							{
								continue;
							}
							
							if (point.name == injectionPoint)
							{
								foreach (var sp in point.shaderProperties)
								{
									list.Add(sp);
								}
							}
						}
					}

					return list;
				}

				static float[] ExtractDefaultValue(string input)
				{
					List<float> list = new List<float>();
					string current = "";

					for (int i = 0; i < input.Length; i++)
					{
						if (char.IsDigit(input[i]) || input[i] == '.')
						{
							current += input[i];
						}
						else
						{
							if (current != "")
							{
								list.Add(float.Parse(current, CultureInfo.InvariantCulture));
								current = "";
							}
						}
					}

					return list.ToArray();
				}
			}
		}
	}
}

// -----------------------------------------------------------------------------
// Merged from Template.cs
// -----------------------------------------------------------------------------

// Represents a shader Template for the Shader Generator

namespace ToonyColorsPro
{
	namespace ShaderGenerator
	{
		internal class Template
		{
			internal static Template CurrentTemplate;

			internal TextAsset textAsset { get; private set; }
			internal bool valid { get; private set; }
			internal string[] originalTextLines;    //text lines with the MODULES keywords
			internal string[] textLines;            //text lines after being processed for the MODULES
			internal string templateInfo;
			internal string templateWarning;
			internal string templateType;
			internal string[] templateKeywords;
			internal string id;
			internal UIFeature[] uiFeatures;
			internal ShaderProperty[] shaderProperties;
			internal List<InjectionPoint> injectionPoints;

			internal class InjectionPoint
			{
				public string name;
				public ShaderProperty.ProgramType program = ShaderProperty.ProgramType.Undefined;
			}

			internal Template()
			{
				TryLoadTextAsset();
			}

			internal void SetTextAsset(TextAsset templateAsset)
			{
				valid = false;
				textAsset = templateAsset;
				if (templateAsset != null)
				{
					var assetPath = AssetDatabase.GetAssetPath(templateAsset);
					var osPath = Application.dataPath + "/" + assetPath.Substring("Assets/".Length);

					// verify that it's a valid SG2 template
					var lines = File.ReadAllLines(osPath);
					for (int i = 0; i < lines.Length; i++)
					{
						if (lines[i].StartsWith("#SG2"))
						{
							valid = true;
							break;
						}

						if (lines[i].StartsWith("#FEATURES"))
						{
							break;
						}
					}

					if (valid)
					{
						originalTextLines = lines;
					}

					UpdateTemplateMeta();
				}
			}

			internal void Reload()
			{
				UpdateTemplateMeta();
			}

			internal void ApplyForcedValues(Config config)
			{
				foreach (var uiFeature in uiFeatures)
				{
					uiFeature.ForceValue(config);
				}
			}

			internal void ApplyKeywords(Config config)
			{
				// clear previous keywords
				for (int i = config.Features.Count-1; i >= 0; i--)
				{
					if (config.Features[i].StartsWith("TEMPLATE_"))
					{
						config.Features.RemoveAt(i);
					}
				}

				if (templateKeywords == null)
				{
					return;
				}

				// add new keywords if any
				foreach (var kw in templateKeywords)
				{
					Utils.AddIfMissing(config.Features, kw);
				}
			}

			internal void FeaturesGUI(Config config)
			{
				if (uiFeatures == null)
				{
					EditorGUILayout.HelpBox("Couldn't parse the features from the Template.", MessageType.Error);
					return;
				}

				//Make the template accessible to UIFeatures (so that DropDown can iterate and know if any features inside are modified)
				CurrentTemplate = this;
				var length = uiFeatures.Length;
				for (var i = 0; i < length; i++)
				{
					uiFeatures[i].DrawGUI(config);
				}
			}

			//Try to load a Template according to a config type and/or file
			internal void TryLoadTextAsset(Config config = null)
			{
				var configFile = config != null ? config.templateFile : null;

				//Append file extension if necessary
				if (!string.IsNullOrEmpty(configFile) && !configFile.EndsWith(".txt"))
				{
					configFile = configFile + ".txt";
				}

				TextAsset loadedTextAsset = null;

				if (!string.IsNullOrEmpty(configFile))
				{
					var conf = LoadTextAsset(configFile);
					if (conf != null)
					{
						loadedTextAsset = conf;
						if (loadedTextAsset != null)
						{
							SetTextAsset(loadedTextAsset);
							return;
						}
					}
				}

				string defaultTemplate = "SG2_Template_Default.txt";
#if UNITY_2019_3_OR_NEWER
				if (Shader.globalRenderPipeline.Contains("UniversalPipeline"))
				{
					defaultTemplate = "SG2_Template_URP.txt";
				}
				else if (Shader.globalRenderPipeline == "LightweightPipeline")
				{
					defaultTemplate = "SG2_Template_LWRP.txt";
				}
#elif UNITY_5_6_OR_NEWER

				if (Shader.globalRenderPipeline == "LightweightPipeline")
				{
					defaultTemplate = "SG2_Template_LWRP.txt";
				}
#endif
				loadedTextAsset = LoadTextAsset(defaultTemplate);
				if (loadedTextAsset != null)
				{
					SetTextAsset(loadedTextAsset);
				}
			}

			//Returns an array of parsed lines based on the current features enabled, with their corresponding original line number (for error reporting)
			//Only keeps the lines necessary to generate the shader source, e.g. #FEATURES will be skipped
			//Conditions are now only processed in this function, all the other code should ignore them
			readonly List<ParsedLine> cachedParsedLines = new List<ParsedLine>();
			internal ParsedLine[] GetParsedLinesFromConditions(Config config, List<string> flags, Dictionary<string, List<string>> extraFlags)
			{
				// var list = new List<ParsedLine>();
				cachedParsedLines.Clear();
				var list = cachedParsedLines;

				int depth = -1;
				var stack = new List<bool>();
				var done = new List<bool>();
				var features = new List<string>(config.Features);
				int passIndex = -1;

				//clear optional features from shader properties options
				config.ClearShaderPropertiesFeatures();

				//make sure to use all needed features as config features for conditions
				var conditionFeatures = new List<string>(config.GetShaderPropertiesNeededFeaturesAll());
				conditionFeatures.AddRange(config.Features);
				conditionFeatures.AddRange(config.ExtraTempFeatures);
				
				// save persistent terrain features so that they will also be applied to the BaseGen shader
				foreach (string feature in conditionFeatures)
				{
					if (feature.StartsWith("USE_TERRAIN"))
					{
						ShaderGenerator2.TerrainPersistentKeywords.Add(feature);
					}
				}

				//make sure keywords have been processed
				var keywordsFeatures = new List<string>();
				ProcessKeywordsBlock(config, conditionFeatures, keywordsFeatures, flags, extraFlags);
				features.AddRange(keywordsFeatures);

				//before first #PASS tag: use needed features from _all_ passes:
				//this is to make sure that the CGINCLUDE block with needed #VARIABLES:MODULES gets processed correctly
				features.AddRange(config.GetShaderPropertiesNeededFeaturesAll());
				features.AddRange(config.GetHooksNeededFeatures());
				features.AddRange(config.GetCodeInjectionNeededFeatures());

				//parse lines and strip based on conditions
				for (var i = 0; i < textLines.Length; i++)
				{
					var line = textLines[i];

					if (line.Length > 0 && line[0] == '#')
					{
						if (line.StartsWith("#PASS"))
						{
							//new pass: get the specific features for this pass
							passIndex++;
							features = new List<string>(config.Features);
							features.AddRange(config.GetHooksNeededFeatures());
							features.AddRange(config.GetCodeInjectionNeededFeatures());
							features.AddRange(config.GetShaderPropertiesNeededFeaturesForPass(passIndex));

							var passKeywordsFeatures = new List<string>();
							ProcessKeywordsBlock(config, features, passKeywordsFeatures, flags, extraFlags);
							features.AddRange(passKeywordsFeatures);
						}

						//Skip #FEATURES block
						if (line.StartsWith("#FEATURES"))
						{
							while (i < textLines.Length)
							{
								i++;
								line = textLines[i];
								if (line == "#END")
									break;
							}
						}
					}

					//Conditions
					if (IsConditionLine(ref line))
					{
						if (line.Contains("/// IF_KEYWORD "))
						{
							string keyword = line.Substring(line.IndexOf("/// IF_KEYWORD ") + "/// IF_KEYWORD ".Length);
							bool condition = config.HasKeyword(keyword) && !string.IsNullOrEmpty(config.GetKeyword(keyword));
							stack.Add(condition);
							done.Add(condition);
							depth++;
						}
						else
						{
							var error = ExpressionParser.ProcessCondition(line, features, ref depth, ref stack, ref done);
							if (!string.IsNullOrEmpty(error))
							{
								Debug.LogError(ShaderGenerator2.ErrorMsg(error + "\n@ line " + i));
							}
						}
					}
					//Regular line
					else
					{
						//Append line if inside valid condition block
						if ((depth >= 0 && stack[depth]) || depth < 0)
						{
							list.Add(new ParsedLine { line = line, lineNumber = i + 1 });
						}
					}
				}

				//error?
				if (depth >= 0)
				{
					//Analyze and try to find where the issue is
					var st = new Stack<ParsedLine>();
					for (var i = 0; i < textLines.Length; i++)
					{
						var tline = textLines[i].TrimStart();

						if (tline == "///")
							st.Pop();
						else if (tline.StartsWith("/// IF"))
							st.Push(new ParsedLine { line = textLines[i], lineNumber = i + 1 });
					}

					if (st.Count > 0)
					{
						var pl = st.Pop();
						Debug.LogError(ShaderGenerator2.ErrorMsg(string.Format("Missing {0} ending '///' tag{1} at line {2}:\n{3}", depth + 1, depth > 0 ? "s" : "", pl.lineNumber, pl.line)));
					}
					else
						Debug.LogError(ShaderGenerator2.ErrorMsg(string.Format("Missing {0} ending '///' tag{1}", depth + 1, depth > 0 ? "s" : "")));
				}

				return list.ToArray();
			}

			internal struct ParsedLine
			{
				internal string line;
				internal int lineNumber;

				public override string ToString()
				{
					return line;
				}
			}

			//--------

			private static TextAsset LoadTextAsset(string filename)
			{
				string rootPath = Utils.FindReadmePath(true);
				var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(string.Format("{0}/Editor/Shader Templates/{1}", rootPath, filename));

				if (asset == null)
				{
					var filenameNoExtension = Path.GetFileNameWithoutExtension(filename);
					var guids = AssetDatabase.FindAssets(string.Format("{0} t:TextAsset", filenameNoExtension));
					if (guids.Length >= 1)
					{
						var path = AssetDatabase.GUIDToAssetPath(guids[0]);
						asset = AssetDatabase.LoadAssetAtPath(path, typeof(TextAsset)) as TextAsset;
					}
					else
					{
						Debug.LogError(ShaderGenerator2.ErrorMsg(string.Format("Can't find template using Unity's search system. Make sure that the file '{0}' is in the project!", filename)));
					}
				}

				return asset;
			}

			static void AddRangeWithIndent(List<string> list, string[] lines, string indent)
			{
				for (int i = 0; i < lines.Length; i++)
				{
					if (lines[i].StartsWith("#") && lines[i].Contains("_IMPL"))
					{
						// make sure #ENABLE_IMPL & #DISABLE_IMPL don't get indented, else they will end up in shader source
						list.Add(lines[i]);
					}
					else
					{
						list.Add(indent + lines[i]);
					}
				}
			}

			private void UpdateTemplateMeta()
			{
				uiFeatures = null;
				templateInfo = null;
				templateWarning = null;
				templateType = null;
				templateKeywords = null;
				id = null;
				injectionPoints = new List<InjectionPoint>();

				UIFeature.ClearFoldoutStack();

				if (textAsset != null && !string.IsNullOrEmpty(textAsset.text))
				{
					//First pass: parse #MODULES and replace related keywords
					var newTemplateLines = new List<string>();
					Dictionary<string, Module> modules = new Dictionary<string, Module>();
					var usedModulesVariables = new HashSet<Module>();
					var usedModulesVariablesOutsideCBuffer = new HashSet<Module>();
					var usedModulesFunctions = new HashSet<Module>();
					var usedModulesInput = new HashSet<Module>();
					for (int i = 0; i < originalTextLines.Length; i++)
					{
						string line = originalTextLines[i];

						//Parse #MODULES
						if (line.StartsWith("#MODULES"))
						{
							//Iterate module names and try to find matching TextAssets
							while (line != "#END" && i < originalTextLines.Length)
							{
								line = originalTextLines[i];
								i++;

								if (line == "#END")
									break;

								if (line.StartsWith("//") || line.StartsWith("#") || string.IsNullOrEmpty(line))
									continue;

								try
								{
									var moduleName = line.Trim();
									var module = Module.CreateFromName(moduleName);
									if (module != null)
									{
										modules.Add(moduleName, module);
									}
								}
								catch (Exception e)
								{
									Debug.LogError(ShaderGenerator2.ErrorMsg(string.Format("Parsing error in <b>#MODULES</b> block:\nLine: '{0}'\n'{1}'\n{2}", line, e.Message, e.StackTrace)));
								}
							}
						}

						//Replace module keywords
						if (line.Trim().StartsWith("[[MODULE") && i < originalTextLines.Length)
						{
							//extract indentation
							var indent = "";
							foreach (var c in line)
							{
								if (char.IsWhiteSpace(c))
									indent += c;
								else
									break;
							}

							var start = line.IndexOf("[[MODULE:");
							var end = line.LastIndexOf("]]");
							var tag = line.Substring(start + "[[MODULE:".Length, end - start - "[[MODULE:".Length);

							var moduleName = "";
							var key = "";
							if (tag.IndexOf(':') > 0)
							{
								moduleName = tag.Substring(tag.IndexOf(':') + 1);

								//remove arguments if any
								if (moduleName.Contains("("))
								{
									moduleName = moduleName.Substring(0, moduleName.IndexOf("("));
								}

								//extract key, if any
								int keyStart = moduleName.IndexOf(':');
								if (keyStart > 0)
								{
									key = moduleName.Substring(keyStart+1);
									moduleName = moduleName.Substring(0, keyStart);
								}
							}

							if (!string.IsNullOrEmpty(moduleName) && !modules.ContainsKey(moduleName))
							{
								Debug.LogError(ShaderGenerator2.ErrorMsg(string.Format("Can't find module: '{0}' for '{1}'", moduleName, line.Trim())));
								continue;
							}

							if (tag.StartsWith("INPUT:"))
							{
								//Print Input block from specific module
								foreach (var module in modules.Values)
								{
									if (module.name == moduleName)
									{
										AddRangeWithIndent(newTemplateLines, module.InputStruct, indent);
										usedModulesInput.Add(module);
									}
								}
							}
							else if (tag == "INPUT")
							{
								//Print all Input lines from all modules
								foreach (var module in modules.Values)
								{
									if (!usedModulesInput.Contains(module))
									{
										AddRangeWithIndent(newTemplateLines, module.InputStruct, indent);
									}
								}
							}
							else if (tag.StartsWith("FUNCTIONS:"))
							{
								//Print Functions line from specific module
								foreach (var module in modules.Values)
								{
									if (module.name == moduleName)
									{
										AddRangeWithIndent(newTemplateLines, module.Functions, indent);
										usedModulesFunctions.Add(module);
									}
								}
							}
							else if (tag == "FUNCTIONS")
							{
								//Print all Variables lines from all modules
								foreach (var module in modules.Values)
								{
									if (!usedModulesFunctions.Contains(module) && !module.ExplicitFunctionsDeclaration)
									{
										AddRangeWithIndent(newTemplateLines, module.Functions, indent);
									}
								}
							}
							else if (tag.StartsWith("VARIABLES:"))
							{
								//Print Variables line from specific module
								foreach (var module in modules.Values)
								{
									if (module.name == moduleName)
									{
										AddRangeWithIndent(newTemplateLines, module.Variables, indent);
										usedModulesVariables.Add(module);
									}
								}
							}
							else if (tag.StartsWith("VARIABLES_OUTSIDE_CBUFFER:"))
							{
								//Print Variables line from specific module
								foreach (var module in modules.Values)
								{
									if (module.name == moduleName)
									{
										AddRangeWithIndent(newTemplateLines, module.VariablesOutsideCBuffer, indent);
										usedModulesVariablesOutsideCBuffer.Add(module);
									}
								}
							}
							else if (tag == "VARIABLES")
							{
								//Print all Variables lines from all modules
								foreach (var module in modules.Values)
								{
									if (!usedModulesVariables.Contains(module))
									{
										AddRangeWithIndent(newTemplateLines, module.Variables, indent);
									}
								}
							}
							else if (tag == "VARIABLES_OUTSIDE_CBUFFER")
							{
								//Print all Variables lines from all modules
								foreach (var module in modules.Values)
								{
									if (!usedModulesVariablesOutsideCBuffer.Contains(module))
									{
										AddRangeWithIndent(newTemplateLines, module.VariablesOutsideCBuffer, indent);
									}
								}
							}
							else if (tag == "KEYWORDS")
							{
								//Print all Keywords lines from all modules
								foreach (var module in modules.Values)
								{
									AddRangeWithIndent(newTemplateLines, module.Keywords, indent);
								}
							}
							else if (tag.StartsWith("FEATURES:"))
							{
								AddRangeWithIndent(newTemplateLines, modules[moduleName].Features, indent);
							}
							else if (tag.StartsWith("PROPERTIES_NEW:"))
							{
								AddRangeWithIndent(newTemplateLines, modules[moduleName].PropertiesNew, indent);
							}
							else if (tag.StartsWith("PROPERTIES_BLOCK:"))
							{
								AddRangeWithIndent(newTemplateLines, modules[moduleName].PropertiesBlock, indent);
							}
							else if (tag.StartsWith("SHADER_FEATURES_BLOCK"))
							{
								AddRangeWithIndent(newTemplateLines, modules[moduleName].ShaderFeaturesBlock, indent);
							}
							else if (tag.StartsWith("VERTEX:"))
							{
								//Get arguments if any
								var args = new List<string>();
								int argStart = tag.IndexOf("(") + 1;
								int argEnd = tag.IndexOf(")");
								if (argStart > 0 && argEnd > 0)
								{
									string arguments = tag.Substring(argStart, argEnd - argStart);
									var argumentsSplit = arguments.Split(',');
									foreach (var a in argumentsSplit)
										args.Add(a.Trim());
								}

								AddRangeWithIndent(newTemplateLines, modules[moduleName].VertexLines(args, key), indent);
							}
							else if (tag.StartsWith("FRAGMENT:"))
							{
								//Get arguments if any
								var args = new List<string>();
								int argStart = tag.IndexOf("(") + 1;
								int argEnd = tag.IndexOf(")");
								if (argStart > 0 && argEnd > 0)
								{
									string arguments = tag.Substring(argStart, argEnd - argStart);
									var argumentsSplit = arguments.Split(',');
									foreach (var a in argumentsSplit)
										args.Add(a.Trim());
								}

								AddRangeWithIndent(newTemplateLines, modules[moduleName].FragmentLines(args, key), indent);
							}
							else
							{
								string blockName = tag.Substring(0, tag.LastIndexOf(":", StringComparison.Ordinal));
								var blockLines = modules[moduleName].GetArbitraryBlock(blockName);
								if (blockLines != null)
								{
									AddRangeWithIndent(newTemplateLines, blockLines.ToArray(), "");
								}
							}
						}
						else
						{
							newTemplateLines.Add(line);
						}
					}

					// Check unused explicit modules functions
					foreach (var module in modules.Values)
					{
						if (module.ExplicitFunctionsDeclaration && !usedModulesFunctions.Contains(module))
						{
							Debug.LogWarning("Module has explicit functions declaration, but isn't used: " + module.name);
						}
					}

					//Apply to textLines
					this.textLines = newTemplateLines.ToArray();

					//Second pass: parse other blocks
					for (int i = 0; i < textLines.Length; i++)
					{
						var line = textLines[i];
						if (line.StartsWith("#INFO="))
						{
							templateInfo = line.Substring("#INFO=".Length).TrimEnd().Replace("  ", "\n");
						}

						else if (line.StartsWith("#WARNING="))
						{
							templateWarning = line.Substring("#WARNING=".Length).TrimEnd().Replace("  ", "\n");
						}

						else if (line.StartsWith("#CONFIG="))
						{
							templateType = line.Substring("#CONFIG=".Length).TrimEnd().ToLower();
						}

						else if (line.StartsWith("#TEMPLATE_KEYWORDS="))
						{
							templateKeywords = line.Substring("#TEMPLATE_KEYWORDS=".Length).TrimEnd().Split(',');
						}

						else if (line.StartsWith("#ID="))
						{
							id = line.Substring("#ID=".Length).TrimEnd();
						}

						else if (line.StartsWith("#FEATURES"))
						{
							uiFeatures = UIFeature.GetUIFeatures(textLines, ref i, this);
						}

						else if (line.StartsWith("#PROPERTIES_NEW"))
						{
							shaderProperties = GetShaderProperties(textLines, i);
							return;
						}

						//Config meta should appear before the Shader name line
						else if (line.StartsWith("Shader"))
						{
							return;
						}
					}

					if (id == null)
					{
						Debug.LogWarning(ShaderGenerator2.ErrorMsg("Missing ID in template metadata!"));
					}
				}
			}

			//Get all Shader Properties regardless of conditions, only their visibility will be affected by the Config
			//This ensures that they are always in the correct order
			//Also link the pending Imp_ShaderPropertyReferences at this time, if any
			//and assign the correct pass bitmask based on usage
			static ShaderProperty[] GetShaderProperties(string[] lines, int i)
			{
				var shaderPropertiesList = new List<ShaderProperty>();
				string subline;
				do
				{
					subline = lines[i];
					i++;

					if (subline == "#END")
						break;

					if (subline.Trim().StartsWith("//") || subline.StartsWith("#") || string.IsNullOrEmpty(subline))
						continue;

					if (subline.Trim().StartsWith("header"))
						continue;

					try
					{
						var shaderProperty = ShaderProperty.CreateFromTemplateData(subline);
						shaderPropertiesList.Add(shaderProperty);
					}
					catch (Exception e)
					{
						Debug.LogError(ShaderGenerator2.ErrorMsg(string.Format("Parsing error in <b>#PROPERTIES_NEW</b> block:\n\nError: '{0}'\n\nLine: '{1}'", e.ToString(), subline)));
					}
				}
				while (subline != "#END" && subline != null);

				//link shader property references
				foreach (var shaderProperty in shaderPropertiesList)
				{
					if (shaderProperty.implementations != null && shaderProperty.implementations.Count > 0)
					{
						foreach (var imp in shaderProperty.implementations)
						{
							var impSpRef = imp as ShaderProperty.Imp_ShaderPropertyReference;
							if (impSpRef != null && !string.IsNullOrEmpty(impSpRef.LinkedShaderPropertyName))
							{
								var match = shaderPropertiesList.Find(sp => sp.Name == impSpRef.LinkedShaderPropertyName);
								if (match != null)
								{
									var channels = impSpRef.Channels;
									impSpRef.LinkedShaderProperty = match;
									//restore channels from template data, it's up to the template to match the referenced shader property
									if (!string.IsNullOrEmpty(channels))
									{
										impSpRef.Channels = channels.ToUpperInvariant();
									}
									impSpRef.ForceUpdateParentDefaultHash();
								}
								else
								{
									Debug.LogError(ShaderGenerator2.ErrorMsg(string.Format("Can't find referenced Shader Property in template.\n'{0}' tried to reference '{1}'", shaderProperty.Name, impSpRef.LinkedShaderPropertyName)));
								}
							}

							var impMpTex = imp as ShaderProperty.Imp_MaterialProperty_Texture;
							if (impMpTex != null && impMpTex.UvSource == ShaderProperty.Imp_MaterialProperty_Texture.UvSourceType.OtherShaderProperty && !string.IsNullOrEmpty(impMpTex.LinkedShaderPropertyName))
							{
								// NOTE: same code as above, with variables changes for materialproperty_tex
								var match = shaderPropertiesList.Find(sp => sp.Name == impMpTex.LinkedShaderPropertyName);
								if (match != null)
								{
									var channels = impMpTex.UVChannels;
									impMpTex.LinkedShaderProperty = match;
									//restore channels from template data, it's up to the template to match the referenced shader property
									if (!string.IsNullOrEmpty(channels))
									{
										impMpTex.UVChannels = channels.ToUpperInvariant();
									}
									impMpTex.ForceUpdateParentDefaultHash();
								}
								else
								{
									Debug.LogError(ShaderGenerator2.ErrorMsg(string.Format("Can't find referenced Shader Property in template.\n'{0}' tried to reference '{1}'", shaderProperty.Name, impMpTex.LinkedShaderPropertyName)));
								}
							}
						}
					}
				}

				//iterate rest of template to check usage of each shader property per pass

				int currentPass = -1;
				for (; i < lines.Length; i++)
				{
					var line = lines[i].Trim();

					// update pass
					if (line.StartsWith("#PASS"))
					{
						currentPass++;
						continue;
					}

					// check value usage: used in which pass(es), and which generic implementation they can use
					var end = 0;
					while (line.IndexOf("[[", end) >= 0)
					{
						var start = line.IndexOf("[[", end);
						end = line.IndexOf("]]", end + 1);
						var tag = line.Substring(start + 2, end - start - 2);
						if (tag.StartsWith("VALUE:") || tag.StartsWith("SAMPLE_VALUE_SHADER_PROPERTY:"))
						{
							var propName = tag.Substring(tag.IndexOf(':') + 1);
							int argsStart = propName.IndexOf('(');
							if (argsStart > 0)
							{
								propName = propName.Substring(0, argsStart);
							}

							var sp = shaderPropertiesList.Find(x => x.Name == propName);
							if (sp != null)
							{
								// found used Shader Property
								sp.AddPassUsage(currentPass);
							}
							else
							{
								Debug.LogError(ShaderGenerator2.ErrorMsg(string.Format("No match for used Shader Property in code: '<b>{0}</b>'", tag)));
							}
						}
					}
				}

				return shaderPropertiesList.ToArray();
			}

			internal ShaderProperty[] GetConditionalShaderProperties(ParsedLine[] parsedLines, out Dictionary<int, GUIContent> headers)
			{
				headers = new Dictionary<int, GUIContent>();

				var shaderPropertiesList = new List<ShaderProperty>();
				for (var i = 0; i < parsedLines.Length; i++)
				{
					var line = parsedLines[i].line;

					if (line.StartsWith("#PROPERTIES_NEW"))
					{
						while (i < parsedLines.Length)
						{
							line = parsedLines[i].line;
							i++;

							if (line.StartsWith("#END"))
								return shaderPropertiesList.ToArray();

							if (line.StartsWith("//") || line.StartsWith("#") || string.IsNullOrEmpty(line))
								continue;

							if (line.Trim().StartsWith("header"))
							{
								var data = line.Split(new string[] { "\t" }, System.StringSplitOptions.RemoveEmptyEntries);
								var gc = new GUIContent(data[1], data.Length > 2 ? data[2].Trim('\"') : null);
								if (!headers.ContainsKey(shaderPropertiesList.Count))
								{
									headers.Add(shaderPropertiesList.Count, null);
								}
								headers[shaderPropertiesList.Count] = gc;	// only take the last one into account, so that empty headers will be ignored
								continue;
							}

							try
							{
								var shaderProperty = ShaderProperty.CreateFromTemplateData(line);
								var match = GetShaderPropertyByName(shaderProperty.Name);
								if (match == null)
									Debug.LogError(ShaderGenerator2.ErrorMsg("Can't find Shader Property in Template, yet it was found for Config"));
								else
									shaderPropertiesList.Add(match);
							}
							catch (Exception e)
							{
								Debug.LogError(ShaderGenerator2.ErrorMsg(string.Format("Parsing error in <b>#PROPERTIES_NEW</b> block:\n'{0}'\n{1}", e.Message, e.StackTrace)));
							}

						}
					}
				}

				return shaderPropertiesList.ToArray();
			}

			internal List<List<ShaderProperty>> FindUsedShaderPropertiesPerPass(ParsedLine[] parsedLines)
			{
				// Find used shader properties depending on the current pass, to extract used features per pass
				var shaderPropertiesPerPass = new List<List<ShaderProperty>>();
				// Find available Generic Implementations based on the current features
				ShaderProperty.Imp_GenericFromTemplate.InitList();
				int passIndex = -1;
				string program = "undefined";

				for (var i = 0; i < parsedLines.Length; i++)
				{
					var line = parsedLines[i].line.Trim();

					if (line.Length > 0 && line[0] == '#')
					{
						if (line.StartsWith("#PASS"))
						{
							passIndex++;
							shaderPropertiesPerPass.Add(new List<ShaderProperty>());
							continue;
						}

						if (line.StartsWith("#VERTEX"))
						{
							program = "vertex";
							continue;
						}

						if (line.StartsWith("#FRAGMENT"))
						{
							program = "fragment";
							continue;
						}

						if (line.StartsWith("#LIGHTING"))
						{
							program = "lighting";
							continue;
						}

						if (passIndex < 0)
						{
							continue;
						}

						// enabled generic implementation
						if (line.StartsWith("#ENABLE_IMPL"))
						{
							ShaderProperty.Imp_GenericFromTemplate.EnableFromLine(line, passIndex, program);
							continue;
						}
						// disabled generic implementation
						if (line.StartsWith("#DISABLE_IMPL"))
						{
							if (line.Contains("DISABLE_IMPL_ALL"))
							{
								ShaderProperty.Imp_GenericFromTemplate.DisableAll();
							}
							else
							{
								ShaderProperty.Imp_GenericFromTemplate.DisableFromLine(line, passIndex, program);
							}
							continue;
						}
					}

					var end = 0;
					while (line.IndexOf("[[", end) >= 0)
					{
						var start = line.IndexOf("[[", end);
						end = line.IndexOf("]]", end + 1);
						var tag = line.Substring(start + 2, end - start - 2);
						if (tag.StartsWith("VALUE:") || tag.StartsWith("SAMPLE_VALUE_SHADER_PROPERTY:"))
						{
							var propName = tag.Substring(tag.IndexOf(':') + 1);
							int argsStart = propName.IndexOf('(');
							if (argsStart > 0)
							{
								propName = propName.Substring(0, argsStart);
							}

							var sp = GetShaderPropertyByName(propName);
							if (sp != null)
							{
								//add to used Shader Properties for current parsed pass
								if (!shaderPropertiesPerPass[passIndex].Contains(sp))
								{
									shaderPropertiesPerPass[passIndex].Add(sp);
								}

								ShaderProperty.Imp_GenericFromTemplate.AddCompatibleShaderProperty(sp);
							}
							else
							{
								Debug.LogError(ShaderGenerator2.ErrorMsg(string.Format("No match for used Shader Property in code: '<b>{0}</b>'", tag)));
							}
						}

						if (tag.StartsWith("INJECTION_POINT:"))
						{
							string injectionPoint = tag.Substring(tag.IndexOf(":") + 1);

							var list = CodeInjectionManager.instance.GetShaderPropertiesForInjectionPoint(injectionPoint);

							foreach (var sp in list)
							{
								if (passIndex >= 0 && passIndex < shaderPropertiesPerPass.Count && !shaderPropertiesPerPass[passIndex].Contains(sp))
								{
									shaderPropertiesPerPass[passIndex].Add(sp);
								}
							}
						}
					}
				}

				ShaderProperty.Imp_GenericFromTemplate.ListCompleted();

				// Iterate through properties, and take into account referenced ones
				Action<ShaderProperty, List<ShaderProperty>> findAndAddLinkedShaderProperties = null;
				findAndAddLinkedShaderProperties = (sp, list) =>
				{
					foreach (var imp in sp.implementations)
					{
						var impSpRef = imp as ShaderProperty.Imp_ShaderPropertyReference;
						if (impSpRef != null)
						{
							// linked shader property can't be null during compilation, or something went wrong
							if (!list.Contains(impSpRef.LinkedShaderProperty))
							{
								list.Add(impSpRef.LinkedShaderProperty);

								// recursive
								findAndAddLinkedShaderProperties(impSpRef.LinkedShaderProperty, list);
							}
						}
						var impMpTex = imp as ShaderProperty.Imp_MaterialProperty_Texture;
						if (impMpTex != null && impMpTex.UvSource == ShaderProperty.Imp_MaterialProperty_Texture.UvSourceType.OtherShaderProperty)
						{
							if (impMpTex.LinkedShaderProperty == null)
							{
								continue;
							}

							if (!list.Contains(impMpTex.LinkedShaderProperty))
							{
								list.Add(impMpTex.LinkedShaderProperty);

								// recursive
								findAndAddLinkedShaderProperties(impMpTex.LinkedShaderProperty, list);
							}
						}
					}
				};
				for (int i = 0; i < shaderPropertiesPerPass.Count; i++)
				{
					var list = shaderPropertiesPerPass[i];
					foreach (var sp in list.ToArray())
					{
						findAndAddLinkedShaderProperties(sp, list);
					}
				}

				return shaderPropertiesPerPass;
			}

			internal void UpdateInjectionPoints(ParsedLine[] parsedLines)
			{
				injectionPoints = new List<InjectionPoint>();

				if (textAsset != null && !string.IsNullOrEmpty(textAsset.text))
				{
					var currentProgram = ShaderProperty.ProgramType.Undefined;
					for (int i = 0; i < parsedLines.Length; i++)
					{
						string line = parsedLines[i].line;

						if (line.Length > 0 && line[0] == '#')
						{
							// Get current program type
							if (line.StartsWith("#PASS"))
							{
								currentProgram = ShaderProperty.ProgramType.Undefined;
							}
							else if (line.StartsWith("#VERTEX"))
							{
								currentProgram = ShaderProperty.ProgramType.Vertex;
							}
							else if (line.StartsWith("#FRAGMENT") || line.StartsWith("#LIGHTING"))
							{
								currentProgram = ShaderProperty.ProgramType.Fragment;
							}
						}
						else if (line.Contains("INJECTION_POINT:"))
						{
							int start = line.IndexOf("INJECTION_POINT:") + "INJECTION_POINT:".Length;
							int end = line.LastIndexOf("]]");
							string injectionName = line.Substring(start, end - start);

							injectionPoints.Add(new InjectionPoint()
							{
								name = injectionName,
								program = currentProgram
							});
						}
					}
				}
			}

			ShaderProperty GetShaderPropertyByName(string name)
			{
				return Array.Find(shaderProperties, sp => sp.Name == name);
			}

			public void ResetShaderProperties()
			{
				foreach (var sp in shaderProperties)
				{
					sp.ResetDefaultImplementation();
				}
			}

			//Process the #KEYWORDS block for this config
			internal void ProcessKeywordsBlock(Config config, List<string> conditionalFeatures, List<string> tempFeatures, List<string> tempFlags, Dictionary<string, List<string>> tempExtraFlags)
			{
				var depth = -1;
				var stack = new List<bool>();
				var done = new List<bool>();

				for (var i = 0; i < textLines.Length; i++)
				{
					var line = textLines[i];

					if (line.Length <= 0 || line[0] != '#')
					{
						continue;
					}

					if (line.StartsWith("#KEYWORDS"))
					{
						int keywordsStartIndex = i+1;

						while (i < textLines.Length)
						{
							line = textLines[i];
							i++;

							if (line.Length > 0 && line[0] == '#' && line.StartsWith("#END"))
							{
								return;
							}

							//Conditions
							if (IsConditionLine(ref line))
							{
								if (line.Contains("/// IF_KEYWORD "))
								{
									string keyword = line.Substring(line.IndexOf("/// IF_KEYWORD ") + "/// IF_KEYWORD ".Length);
									bool condition = config.HasKeyword(keyword) && !string.IsNullOrEmpty(config.GetKeyword(keyword));
									stack.Add(condition);
									done.Add(condition);
									depth++;
								}
								else
								{
									var error = ExpressionParser.ProcessCondition(line, conditionalFeatures, ref depth, ref stack, ref done);
									if (!string.IsNullOrEmpty(error))
									{
										Debug.LogError(ShaderGenerator2.ErrorMsg(error));
									}
								}
							}
							//Regular line
							else
							{
								//Process line if inside valid condition block
								if ((depth >= 0 && stack[depth]) || depth < 0)
								{
									if (config.ProcessKeywords(line, tempFeatures, tempFlags, tempExtraFlags))
									{
										// add the new toggled features, if any
										foreach (var f in tempFeatures)
										{
											Utils.AddIfMissing(conditionalFeatures, f);
										}

										// reset the loop, so that the #keywords order doesn't matter in the template
										i = keywordsStartIndex;
										continue;
									}
								}
							}
						}
					}
				}
			}

			//Find out if current pass has a lighting function, to know if we need to generate surface output variables
			internal bool PassIsSurfaceShader(ParsedLine[] parsedLines, int pass)
			{
				int passIndex = -1;

				for (var i = 0; i < parsedLines.Length; i++)
				{
					var line = parsedLines[i].line.Trim();

					if(line.Length == 0 || line[0] != '#')
					{
						continue;
					}

					if (line.StartsWith("#PASS"))
					{
						passIndex++;
						if (passIndex > pass)
						{
							return false;
						}
					}

					if (passIndex == pass && line.Contains("#pragma surface"))
					{
						return true;
					}
				}
				return false;
			}

			//Process the #INPUT block: retrieve all necessary variables
			//for Input struct (surface shader) or v2f struct (vert/frag shader)
			internal string[] GetInputBlock(ParsedLine[] parsedLines, int pass)
			{
				var variablesList = new List<string>();
				int currentPass = -1;

				for (var i = 0; i < parsedLines.Length; i++)
				{
					var line = parsedLines[i].line;

					if (line.StartsWith("#PASS"))
						currentPass++;

					if (line.StartsWith("#INPUT_VARIABLES") && currentPass == pass)
					{
						i++;
						while (i < parsedLines.Length)
						{
							line = parsedLines[i].line;
							i++;

							if (line.StartsWith("#END"))
								return variablesList.ToArray();

							if (line.StartsWith("#") || string.IsNullOrEmpty(line.Trim()))
								continue;

							//Conditions
							if (IsConditionLine(ref line))
							{
								Debug.LogError(ShaderGenerator2.ErrorMsg("GetInputBlock: template lines should already have been parsed and cleared of conditions"));
							}
							//Regular line
							else
							{
								variablesList.Add(line.Trim());
							}
						}
					}
				}

				return null;
			}

			// Checks if the line contains /// and is thus a condition line
			// Faster than string.Contains("///"), and is called a lot
			static bool IsConditionLine(ref string line)
			{
				bool isCondition = false;
				int slashCount = 0;
				for (int c = 0; c < line.Length; c++)
				{
					if (line[c] == ' ' || line[c] == '\t')
					{
						if (slashCount == 3)
						{
							isCondition = true;
							break;
						}
							
						if (slashCount > 0)
						{
							break;
						}
					}
					else if (line[c] == '/')
					{
						slashCount++;
					}
					else
					{
						break;
					}
				}

				isCondition |= slashCount == 3;
				return isCondition;
			}
		}
	}
}

// -----------------------------------------------------------------------------
// Merged from Config.cs
// -----------------------------------------------------------------------------

// Toony Colors Pro+Mobile 2
// (c) 2014-2026 Jean Moreno

// Represents a Toony Colors Pro 2 configuration to generate the corresponding shader
// (new version for Shader Generator 2)

namespace ToonyColorsPro
{
	namespace ShaderGenerator
	{
		internal interface IMaterialPropertyName { string GetPropertyName(); }

		internal static class UniqueMaterialPropertyName
		{
			internal delegate bool CheckUniqueVariableName(string variableName, IMaterialPropertyName materialPropertyName);
			internal static event CheckUniqueVariableName checkUniqueVariableName;

			internal static string GetUniquePropertyName(string baseName, IMaterialPropertyName materialPropertyName)
			{
				if (checkUniqueVariableName == null)
				{
					return baseName;
				}

				//name doesn't exist: all good
				if (checkUniqueVariableName(baseName, materialPropertyName))
					return baseName;

				//extract the last digits of the name, if any
				for (var i = baseName.Length - 1; i >= 0; i--)
				{
					if (baseName[i] >= '0' && baseName[i] <= '9')
						continue;
					baseName = baseName.Substring(0, i + 1);
					break;
				}

				//check if name is unique: requires a class that registers to the event and supply its own checks
				var newName = baseName;
				var count = 1;
				while (!checkUniqueVariableName(newName, materialPropertyName))
				{
					newName = string.Format("{0}{1}", baseName, count);
					count++;
				}

				return newName;
			}
		}

		[Serialization.SerializeAs("config")]
		internal class Config
		{
#pragma warning disable 414
			[Serialization.SerializeAs("ver")] string tcp2version { get { return ShaderGenerator2.TCP2_VERSION; } }
			[Serialization.SerializeAs("unity")] string unityVersion { get { return Application.unityVersion; } }
#pragma warning restore 414

			internal const string kSerializationPrefix = "/* TCP_DATA ";
			internal const string kSerializationPrefixUncompressed = "/* TCP_DATA u ";
			internal const string kSerializationSuffix = " */";

			internal const string kHashPrefix = "/* TCP_HASH ";
			internal const string kHashSuffix = " */";

			internal string Filename = "My TCP2 Shader";
			internal string ShaderName = "Toony Colors Pro 2/User/My TCP2 Shader";
			[Serialization.SerializeAs("tmplt")] internal string templateFile = "TCP2_ShaderTemplate_Default";
			[Serialization.SerializeAs("features")] internal List<string> Features = new List<string>();
			internal List<string> ExtraTempFeatures = new List<string>();
			[Serialization.SerializeAs("flags")] internal List<string> Flags = new List<string>();
			[Serialization.SerializeAs("flags_extra")] internal Dictionary<string, List<string>> FlagsExtra = new Dictionary<string, List<string>>();
			[Serialization.SerializeAs("keywords")] internal Dictionary<string, string> Keywords = new Dictionary<string, string>();
			internal bool isModifiedExternally = false;
			internal bool isTerrainShader
			{
				get { return this.Features.Contains("TERRAIN_SHADER"); }
			}

			// UI list of Shader Properties
			struct ShaderPropertyGroup
			{
				public GUIContent header;
				public bool hasModifiedShaderProperties;
				public bool hasErrors;
				public List<ShaderProperty> shaderProperties;
			}
			List<ShaderPropertyGroup> shaderPropertiesUIGroups = new List<ShaderPropertyGroup>();
			Dictionary<string, bool> headersExpanded = new Dictionary<string, bool>(); // the struct array above is always recreated, so we can't track expanded state there
			List<ShaderProperty> visibleShaderProperties = new List<ShaderProperty>();
			//Serialize all cached Shader Properties so that their custom implementation is saved, even if they are not used in the shader
			[Serialization.SerializeAs("shaderProperties")] List<ShaderProperty> cachedShaderProperties = new List<ShaderProperty>();
			List<List<ShaderProperty>> shaderPropertiesPerPass;
			[Serialization.SerializeAs("customTextures")] List<ShaderProperty.CustomMaterialProperty> customMaterialPropertiesList = new List<ShaderProperty.CustomMaterialProperty>();
			ReorderableLayoutList customTexturesLayoutList = new ReorderableLayoutList();

			/// Iterate through all Shader Properties associated with this config, including Material Layers and Code Injection
			IEnumerable<ShaderProperty> IterateAllShaderProperties()
			{
				var processed = new HashSet<ShaderProperty>();
				foreach (var shaderProperty in cachedShaderProperties)
				{
					if (processed.Contains(shaderProperty)) continue;
					
					processed.Add(shaderProperty);
					yield return shaderProperty;
				}
				foreach (var shaderProperty in visibleShaderProperties)
				{
					if (processed.Contains(shaderProperty)) continue;
					
					processed.Add(shaderProperty);
					yield return shaderProperty;
				}
				foreach (var materialLayer in materialLayers)
				{
					if (materialLayer.sourceShaderProperty != null)
					{
						if (processed.Contains(materialLayer.sourceShaderProperty)) continue;
						processed.Add(materialLayer.sourceShaderProperty);
						yield return materialLayer.sourceShaderProperty;
					}
					if (materialLayer.noiseProperty != null)
					{
						if (processed.Contains(materialLayer.noiseProperty)) continue;
						processed.Add(materialLayer.noiseProperty);
						yield return materialLayer.noiseProperty;
					}
					if (materialLayer.contrastProperty != null)
					{
						if (processed.Contains(materialLayer.contrastProperty)) continue;
						processed.Add(materialLayer.contrastProperty);
						yield return materialLayer.contrastProperty;
					}
				}
				foreach (var injectedFile in codeInjection.injectedFiles)
				{
					foreach (var point in injectedFile.injectedPoints)
					{
						foreach (var shaderProperty in point.shaderProperties)
						{
							if (shaderProperty != null)
							{
								if (processed.Contains(shaderProperty)) continue;
								processed.Add(shaderProperty);
								yield return shaderProperty;
							}
						}
					}
				}
			}

			public ShaderProperty customMaterialPropertyShaderProperty = new ShaderProperty("_CustomMaterialPropertyDummy", ShaderProperty.VariableType.color_rgba);

			internal ShaderProperty.CustomMaterialProperty[] CustomMaterialProperties { get { return customMaterialPropertiesList.ToArray(); } }
			internal ShaderProperty[] VisibleShaderProperties { get { return visibleShaderProperties.ToArray(); } }
			internal ShaderProperty[] AllShaderProperties { get { return cachedShaderProperties.ToArray(); } }


			// Code Injection properties
			[Serialization.SerializeAs("codeInjection")] internal CodeInjectionManager codeInjection = new CodeInjectionManager();
			
			// Material Layers
			[Serialization.SerializeAs("matLayers")] internal List<MaterialLayer> materialLayers = new List<MaterialLayer>();

			KeyValuePair<string, string>[] _materialLayersNames;
			internal KeyValuePair<string, string>[] materialLayersNames
			{
				get
				{
					if (_materialLayersNames == null || _materialLayersNames.Length != materialLayers.Count)
					{
						var list = materialLayers.ConvertAll(element => new KeyValuePair<string, string>(element.name, element.uid));
						list.Insert(0, new KeyValuePair<string, string>("Base", null));
						_materialLayersNames = list.ToArray();
					}
					return _materialLayersNames;
				}
			}

			ReorderableLayoutList matLayersLayoutList = new ReorderableLayoutList();

			internal MaterialLayer GetMaterialLayerByUID(string uid)
			{
				return materialLayers.Find(ml => ml.uid == uid);
			}

			internal string[] GetShaderPropertiesNeededFeaturesForPass(int passIndex)
			{
				if (shaderPropertiesPerPass == null || shaderPropertiesPerPass.Count == 0)
					return new string[0];

				if (passIndex >= shaderPropertiesPerPass.Count)
					return new string[0];

				if (shaderPropertiesPerPass[passIndex] == null || shaderPropertiesPerPass[passIndex].Count == 0)
					return new string[0];

				List<string> usedMaterialLayersVertex = new List<string>();
				List<string> usedMaterialLayersFragment = new List<string>();
				var features = new List<string>();
				foreach (var sp in shaderPropertiesPerPass[passIndex])
				{
					features.AddRange(sp.NeededFeatures());
					
					// figure out used MaterialLayers and their programs
					foreach (string uid in sp.linkedMaterialLayers)
					{
						if (sp.Program == ShaderProperty.ProgramType.Vertex)
						{
							if (!usedMaterialLayersVertex.Contains(uid))
							{
								usedMaterialLayersVertex.Add(uid);
							}
						}
						else if (sp.Program == ShaderProperty.ProgramType.Fragment)
						{
							if (!usedMaterialLayersFragment.Contains(uid))
							{
								usedMaterialLayersFragment.Add(uid);
							}
						}
					}
				}
				
				// needed features for Material Layer sources
				// HACK: We override the program type so that the relevant needed features get added.
				//       This is cleaner than refactoring all the methods called.

				Action<ShaderProperty, ShaderProperty.ProgramType> GetNeededFeaturesForProperty = (shaderProperty, programType) =>
				{
					if (shaderProperty == null)
					{
						return;
					}
					
					var program = shaderProperty.Program;
					shaderProperty.Program = programType;
					{
						features.AddRange(shaderProperty.NeededFeatures());
					}
					shaderProperty.Program = program;
				};
				
				foreach (string uid in usedMaterialLayersVertex)
				{
					var ml = this.GetMaterialLayerByUID(uid);
					GetNeededFeaturesForProperty(ml.sourceShaderProperty, ShaderProperty.ProgramType.Vertex);
					GetNeededFeaturesForProperty(ml.contrastProperty, ShaderProperty.ProgramType.Vertex);
					GetNeededFeaturesForProperty(ml.noiseProperty, ShaderProperty.ProgramType.Vertex);
				}
				foreach (string uid in usedMaterialLayersFragment)
				{
					var ml = this.GetMaterialLayerByUID(uid);
					GetNeededFeaturesForProperty(ml.sourceShaderProperty, ShaderProperty.ProgramType.Fragment);
					GetNeededFeaturesForProperty(ml.contrastProperty, ShaderProperty.ProgramType.Fragment);
					GetNeededFeaturesForProperty(ml.noiseProperty, ShaderProperty.ProgramType.Fragment);
				}

				return features.Distinct().ToArray();
			}

			internal string[] GetShaderPropertiesNeededFeaturesAll()
			{
				if (shaderPropertiesPerPass == null || shaderPropertiesPerPass.Count == 0)
				{
					return new string[0];
				}
				
				List<string> features = new List<string>();
				for (int i = 0; i < shaderPropertiesPerPass.Count; i++)
				{
					features.AddRange(GetShaderPropertiesNeededFeaturesForPass(i));
				}
				return features.Distinct().ToArray();
				
				/*
				
				if (shaderPropertiesPerPass == null || shaderPropertiesPerPass.Count == 0)
					return new string[0];

				// iterate through used Shader Properties for all passes and toggle needed features
				List<string> usedMaterialLayers = new List<string>();
				var features = new List<string>();
				foreach (var list in shaderPropertiesPerPass)
				{
					foreach (var sp in list)
					{
						features.AddRange(sp.NeededFeatures());

						foreach (string uid in sp.linkedMaterialLayers)
						{
							if (!usedMaterialLayers.Contains(uid))
							{
								usedMaterialLayers.Add(uid);
							}
						}
					}
				}

				// needed features for Material Layer sources
				foreach (string uid in usedMaterialLayers)
				{
					var ml = this.GetMaterialLayerByUID(uid);
					features.AddRange(ml.sourceShaderProperty.NeededFeatures());
				}

				return features.Distinct().ToArray();
				
				*/
			}

			internal string[] GetHooksNeededFeatures()
			{
				// iterate through Hook Shader Properties and toggle features if needed
				var features = new List<string>();
				foreach (var sp in visibleShaderProperties)
				{
					if (sp.isHook && !string.IsNullOrEmpty(sp.toggleFeatures))
					{
						if (sp.manuallyModified)
						{
							features.AddRange(sp.toggleFeatures.Split(','));
						}
					}
				}
				return features.ToArray();
			}

			internal string[] GetCodeInjectionNeededFeatures()
			{
				return codeInjection.GetNeededFeatures();
			}

			/// <summary>
			/// Remove all features associated with specific Shader Property options,
			/// so that they don't stay when toggling an option on, compile, then off
			/// </summary>
			internal void ClearShaderPropertiesFeatures()
			{
				foreach (var f in ShaderProperty.AllOptionFeatures())
				{
					Utils.RemoveIfExists(this.Features, f);
				}
			}

			//--------------------------------------------------------------------------------------------------

			private enum ParseBlock
			{
				None,
				Features,
				Flags
			}

			internal static Config CreateFromFile(TextAsset asset)
			{
				return CreateFromFile(asset.text);
			}
			internal static Config CreateFromFile(string text)
			{
				var lines = text.Split(new[] { "\n", "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
				var config = new Config();

				//Flags
				var currentBlock = ParseBlock.None;
				for (var i = 0; i < lines.Length; i++)
				{
					var line = lines[i];

					if (line.StartsWith("//")) continue;

					var data = line.Split(new[] { "\t" }, StringSplitOptions.RemoveEmptyEntries);
					if (line.StartsWith("#"))
					{
						currentBlock = ParseBlock.None;

						switch (data[0])
						{
							case "#filename": config.Filename = data[1]; break;
							case "#shadername": config.ShaderName = data[1]; break;
							case "#features": currentBlock = ParseBlock.Features; break;
							case "#flags": currentBlock = ParseBlock.Flags; break;

							default: Debug.LogWarning("[TCP2 Shader Config] Unrecognized tag: " + data[0] + "\nline " + (i + 1)); break;
						}
					}
					else
					{
						if (data.Length > 1)
						{
							var enabled = false;
							bool.TryParse(data[1], out enabled);

							if (enabled)
							{
								if (currentBlock == ParseBlock.Features)
									config.Features.Add(data[0]);
								else if (currentBlock == ParseBlock.Flags)
									config.Flags.Add(data[0]);
								else
									Debug.LogWarning("[TCP2 Shader Config] Unrecognized line while parsing : " + line + "\nline " + (i + 1));
							}
						}
					}
				}

				return config;
			}

			internal static Config CreateFromShader(Shader shader)
			{
				var shaderImporter = ShaderImporter.GetAtPath(AssetDatabase.GetAssetPath(shader)) as ShaderImporter;

				var config = new Config
				{
					ShaderName = shader.name,
					Filename = Path.GetFileName(AssetDatabase.GetAssetPath(shader)).Replace(".shader", "")
				};

				var valid = config.ParseUserData(shaderImporter);
				valid |= config.ParseSerializedDataAndHash(shaderImporter, null, false);    //first run (see method comment)

				if (valid)
					return config;
				return null;
			}

			internal Config Copy()
			{
				var config = new Config
				{
					Filename = Filename,
					ShaderName = ShaderName
				};

				foreach (var feature in Features)
					config.Features.Add(feature);

				foreach (var flag in Flags)
					config.Flags.Add(flag);

				foreach (var kvp in FlagsExtra)
					config.FlagsExtra.Add(kvp.Key, new List<string>(kvp.Value));

				foreach (var kvp in Keywords)
					config.Keywords.Add(kvp.Key, kvp.Value);

				config.templateFile = templateFile;

				config.codeInjection = codeInjection;

				return config;
			}

			//Copy implementations from this config to another
			public void CopyImplementationsTo(Config otherConfig)
			{
				for (int i = 0; i < this.cachedShaderProperties.Count; i++)
				{
					for (int j = 0; j < otherConfig.cachedShaderProperties.Count; j++)
					{
						if (this.cachedShaderProperties[i].Name == otherConfig.cachedShaderProperties[j].Name)
						{
							otherConfig.cachedShaderProperties[j].implementations = this.cachedShaderProperties[i].implementations;
							otherConfig.cachedShaderProperties[j].CheckHash();
							otherConfig.cachedShaderProperties[j].CheckErrors();
							break;
						}
					}
				}

				for (int i = 0; i < otherConfig.cachedShaderProperties.Count; i++)
				{
					otherConfig.cachedShaderProperties[i].ResolveShaderPropertyReferences();
				}
			}

			public void CopyCustomTexturesTo(Config otherConfig)
			{
				otherConfig.customMaterialPropertiesList = this.customMaterialPropertiesList;
				for (int i = 0; i < otherConfig.cachedShaderProperties.Count; i++)
				{
					otherConfig.cachedShaderProperties[i].ResolveShaderPropertyReferences();
				}
			}

			internal bool HasErrors()
			{
				foreach (var shaderProperty in visibleShaderProperties)
				{
					if (shaderProperty.error)
						return true;
				}

				foreach (var customTexture in CustomMaterialProperties)
				{
					if (customTexture.HasErrors)
						return true;
				}

				return false;
			}

			internal string GetConfigFileCustomData()
			{
				return string.Format("CF:{0}", templateFile);
			}

			internal int ToHash()
			{
				var sb = new StringBuilder();
				/*
				sb.Append(Filename);
				sb.Append(ShaderName);
				*/
				var orderedFeatures = new List<string>(Features);
				orderedFeatures.Sort();
				var orderedFlags = new List<string>(Flags);
				orderedFlags.Sort();
				var orderedFlagsExtra = new List<string>();
				foreach (var kvp in FlagsExtra)
					foreach (var flag in kvp.Value)
						orderedFlagsExtra.Add(flag);
				orderedFlagsExtra.Sort();
				var sortedKeywordsKeys = new List<string>(Keywords.Keys);
				sortedKeywordsKeys.Sort();
				var sortedKeywordsValues = new List<string>(Keywords.Values);
				sortedKeywordsValues.Sort();

				foreach (var f in orderedFeatures)
					sb.Append(f);
				foreach (var f in orderedFlags)
					sb.Append(f);
				foreach (var f in sortedKeywordsKeys)
					sb.Append(f);
				foreach (var f in sortedKeywordsValues)
					sb.Append(f);

				foreach (var sp in visibleShaderProperties)
					sb.Append(sp);
				foreach (var ct in customMaterialPropertiesList)
					sb.Append(ct);

				return sb.ToString().GetHashCode();
			}

			bool ParseUserData(ShaderImporter importer)
			{
				if (string.IsNullOrEmpty(importer.userData))
					return false;

				var data = importer.userData.Split(new[] { "," }, StringSplitOptions.RemoveEmptyEntries);
				var customDataList = new List<string>();

				foreach (var d in data)
				{
					if (string.IsNullOrEmpty(d)) continue;

					switch (d[0])
					{
						//Features
						case 'F':
							if (d == "F") break; //Prevent getting "empty" feature
							Features.Add(d.Substring(1));
							break;

						//Flags
						case 'f': Flags.Add(d.Substring(1)); break;

						//Keywords
						case 'K':
							var kw = d.Substring(1).Split(':');
							if (kw.Length != 2)
							{
								Debug.LogError("[TCP2 Shader Generator] Error while parsing userData: invalid Keywords format.");
								return false;
							}
							else
							{
								Keywords.Add(kw[0], kw[1]);
							}
							break;

						//Custom Data
						case 'c': customDataList.Add(d.Substring(1)); break;
						//old format
						default: Features.Add(d); break;
					}
				}

				foreach (var customData in customDataList)
				{
					//Configuration File
					if (customData.StartsWith("CF:"))
					{
						templateFile = customData.Substring(3);
					}
				}

				return true;
			}

			private static string CompressString(string uncompressed)
			{
				var bytes = Encoding.UTF8.GetBytes(uncompressed);
				using (var compressedStream = new MemoryStream())
				{
					using (var gZipStream = new GZipStream(compressedStream, CompressionMode.Compress))
					{
						gZipStream.Write(bytes, 0, bytes.Length);
					}
					bytes = compressedStream.ToArray();
				}
				return Convert.ToBase64String(bytes);
			}

			private static string UncompressString(string compressed)
			{
				var bytes = Convert.FromBase64String(compressed);
				var buffer = new byte[4096];
				var uncompressedStream = new MemoryStream();
				using (var compressedStream = new MemoryStream(bytes))
				{
					using (var gZipStream = new GZipStream(compressedStream, CompressionMode.Decompress))
					{
						var length = 0;
						do
						{
							length = gZipStream.Read(buffer, 0, 4096);
							if (length > 0)
								uncompressedStream.Write(buffer, 0, length);
						}
						while (length > 0);
					}
				}

				return Encoding.UTF8.GetString(uncompressedStream.ToArray());
			}

			//New serialization format, embedded into the shader source in a comment
			internal string GetSerializedData()
			{
				var serialized = Serialization.Serialize(this);
#if WRITE_UNCOMPRESSED_SERIALIZED_DATA
				return kSerializationPrefixUncompressed + serialized + kSerializationSuffix;
#else
				return kSerializationPrefix + CompressString(serialized) + kSerializationSuffix;
#endif
			}

			//This method is executed twice because of an ordering problem:
			// - first run: it needs to fetch the template used from TCP_DATA
			// - then it loads that template and generate the serialized properties
			// - second run: now that the serialized properties exist, replace their implementations with the ones in TCP_DATA
			internal bool ParseSerializedDataAndHash(ShaderImporter importer, Template template, bool dontRebuildCustomTextures)
			{
				//try to find serialized TCP2 data
				var unityPath = importer.assetPath;
				var osPath = Application.dataPath + "/" + unityPath.Substring("Assets/".Length);
				if (File.Exists(osPath))
				{
					var code = File.ReadAllLines(osPath);
					for (var i = code.Length - 1; i >= 0; i--)
					{
						var line = code[i].Trim();
						const string serializedPrefix = kSerializationPrefix;
						const string serializedPrefixU = kSerializationPrefixUncompressed;
						const string serializedSuffix = kSerializationSuffix;

						const string hashPrefix = kHashPrefix;
						const string hashSuffix = kHashSuffix;

						//hash is always inserted after serialized data, so the function shouldn't return without it being checked
						if (line.StartsWith(hashPrefix))
						{
							var hash = line.Substring(hashPrefix.Length, line.Length - hashPrefix.Length - hashSuffix.Length);

							//list of all lines, remove them from the end until the serialized prefix is found
							var codeLines = new List<string>(code);
							for (int j = codeLines.Count - 1; j >= 0; j--)
							{
								bool @break = codeLines[j].StartsWith(hashPrefix);
								codeLines.RemoveAt(j);
								if (@break)
									break;
							}

							var sb = new StringBuilder();
							foreach (var l in codeLines)
							{
								sb.AppendLine(l);
							}
							string normalizedLineEndings = sb.ToString().Replace("\r\n", "\n");
							var fileHash = ShaderGenerator2.GetHash(normalizedLineEndings);

							this.isModifiedExternally = string.Compare(fileHash, hash, StringComparison.Ordinal) != 0;
						}

						if (line.StartsWith(serializedPrefix) || line.StartsWith(serializedPrefixU))
						{
							string extractedData = line;
							int j = i;
							while (!extractedData.Contains(" */") && j < code.Length)
							{
								j++;
								if (j < code.Length)
								{
									line = code[j].Trim();
									extractedData += "\n" + line;
								}
								else
								{
									Debug.LogError(ShaderGenerator2.ErrorMsg("Incomplete serialized data in shader file."));
									return false;
								}
							}

							var serializedData = "";
							if (extractedData.StartsWith(serializedPrefixU))
							{
								serializedData = extractedData.Substring(serializedPrefixU.Length, extractedData.Length - serializedPrefixU.Length - serializedSuffix.Length);
							}
							else
							{
								serializedData = extractedData.Substring(serializedPrefix.Length, extractedData.Length - serializedPrefix.Length - serializedSuffix.Length);
								serializedData = UncompressString(serializedData);
							}

							return ParseSerializedData(serializedData, template, dontRebuildCustomTextures);
						}
					}
				}

				return false;
			}

			public bool ParseSerializedData(string serializedData, Template template, bool dontRebuildCustomTextures, bool resetEmptyImplementations = false)
			{
				Func<object, string, object> onDeserializeShaderPropertyList = (obj, data) =>
				{
					//called with data in format 'list[sp(field:value;field:value...),sp(field:value;...)]'

					// - make a new list, and pull matching sp from it
					// - reset the implementations of the remaining sp for the undo/redo system
					var shaderPropertiesTempList = new List<ShaderProperty>(cachedShaderProperties);

					var split = Serialization.SplitExcludingBlocks(data.Substring(5, data.Length - 6), ',', true, true, "()", "[]");
					foreach (var spData in split)
					{
						//try to match existing Shader Property by its name
						string name = null;

						//exclude 'sp(' and ')' and extract fields
						var vars = Serialization.SplitExcludingBlocks(spData.Substring(3, spData.Length - 4), ';', true, true, "()", "[]");
						foreach (var v in vars)
						{
							//find 'name' and remove 'name:' and quotes to extract value
							if (v.StartsWith("name:"))
								name = v.Substring(6, v.Length - 7);
						}

						if (name != null)
						{
							//find corresponding shader property, if it exists
							var matchedSp = shaderPropertiesTempList.Find(sp => sp.Name == name);

							//if no match, try to find it in the template's shader properties
							if (matchedSp == null && template != null)
							{
								matchedSp = Array.Find(template.shaderProperties, sp => sp.Name == name);
								if (matchedSp != null)
								{
									cachedShaderProperties.Add(matchedSp);
									shaderPropertiesTempList.Add(matchedSp);
								}
							}

							if (matchedSp != null)
							{
								shaderPropertiesTempList.Remove(matchedSp);

								Func<object, string, object> onDeserializeImplementation = (impObj, impData) =>
								{
									return this.DeserializeImplementationHandler(impObj, impData, matchedSp);
								};

								var implementationHandling = new Dictionary<Type, Func<object, string, object>> { { typeof(ShaderProperty.Implementation), onDeserializeImplementation } };

								Serialization.DeserializeTo(matchedSp, spData, typeof(ShaderProperty), null, implementationHandling);

								matchedSp.CheckHash();
								matchedSp.CheckErrors();
							}
						}
					}

					if (resetEmptyImplementations)
					{
						foreach (var remainingShaderProperty in shaderPropertiesTempList)
						{
							remainingShaderProperty.ResetDefaultImplementation();
						}
					}

					return null;
				};

				// try
				{
					var shaderPropertyHandling = new Dictionary<Type, Func<object, string, object>> { { typeof(List<ShaderProperty>), onDeserializeShaderPropertyList } };

					if (dontRebuildCustomTextures)
					{
						// if not building the custom material properties list, just skip its deserialization, else use the custom handling
						shaderPropertyHandling.Add(typeof(List<ShaderProperty.CustomMaterialProperty>), (obj, data) => { return null; });
					}
					Serialization.DeserializeTo(this, serializedData, GetType(), null, shaderPropertyHandling);

					return true;
				}
				// catch (Exception e)
				{
					// Debug.LogError(ShaderGenerator2.ErrorMsg(string.Format("Deserialization error:\n'{0}'\n{1}", e.Message, e.StackTrace.Replace(Application.dataPath, ""))));
					// return false;
				}
			}

			internal object DeserializeImplementationHandler(object impObj, string serializedData, ShaderProperty existingShaderProperty)
			{
				//make sure to deserialize as a new object, so that final Implementation subtype is kept instead of creating base Implementation class
				var imp = Serialization.Deserialize(serializedData, new object[] { existingShaderProperty });

				//if custom material property, find the one with the matching serialized name
				if (imp is ShaderProperty.Imp_CustomMaterialProperty)
				{
					var ict = (imp as ShaderProperty.Imp_CustomMaterialProperty);
					var matchedCt = customMaterialPropertiesList.Find(ct => ct.PropertyName == ict.LinkedCustomMaterialPropertyName);
					//will be the match, or null if nothing found
					ict.LinkedCustomMaterialProperty = matchedCt;
					ict.UpdateChannels();
				}
				else if (imp is ShaderProperty.Imp_ShaderPropertyReference)
				{
					//find existing shader property and link it here
					//TODO: what if the shader property hasn't been deserialized yet?
					var ispr = (imp as ShaderProperty.Imp_ShaderPropertyReference);
					var channels = ispr.Channels;
					var matchedLinkedSp = visibleShaderProperties.Find(sp => sp.Name == ispr.LinkedShaderPropertyName);
					ispr.LinkedShaderProperty = matchedLinkedSp;
					//restore channels from serialized data (it is reset when assigning a new linked shader property)
					if (!string.IsNullOrEmpty(channels))
						ispr.Channels = channels;
				}
				else if (imp is ShaderProperty.Imp_MaterialProperty_Texture)
				{
					// find existing shader property for uv if that option is enabled, and link it
					var impt = (imp as ShaderProperty.Imp_MaterialProperty_Texture);
					var channels = impt.UVChannels;
					var matchedLinkedSp = visibleShaderProperties.Find(sp => sp.Name == impt.LinkedShaderPropertyName);
					impt.LinkedShaderProperty = matchedLinkedSp;
					//restore channels from serialized data (it is reset when assigning a new linked shader property)
					if (!string.IsNullOrEmpty(channels))
						impt.UVChannels = channels;
				}

				return imp;
			}

			internal void AutoNames()
			{
				var rawName = ShaderName.Replace("Toony Colors Pro 2/", "");

				if (!ProjectOptions.data.SubFolders)
				{
					rawName = Path.GetFileName(rawName);
				}

				Filename = rawName;
			}

			//--------------------------------------------------------------------------------------------------
			// FEATURES

			internal bool HasFeature(string feature)
			{
				return Features.Contains(feature);
			}

			internal bool HasFeaturesAny(params string[] features)
			{
				foreach (var f in features)
				{
					if (Features.Contains(f))
					{
						return true;
					}
				}

				return false;
			}

			internal bool HasFeaturesAll(params string[] features)
			{
				foreach (var f in features)
				{
					if (f[0] == '!')
					{
						if (Features.Contains(f.Substring(1)))
						{
							return false;
						}
					}
					else
					{
						if (!Features.Contains(f))
						{
							return false;
						}
					}
				}

				return true;
			}

			internal void ToggleFeature(string feature, bool enable)
			{
				if (string.IsNullOrEmpty(feature))
					return;

				if (!Features.Contains(feature) && enable)
					Features.Add(feature);

				else if (Features.Contains(feature) && !enable)
					Features.Remove(feature);
			}

			//--------------------------------------------------------------------------------------------------
			// FLAGS

			internal bool HasFlag(string block, string flag)
			{
				if (block == "pragma_surface_shader")
				{
					return Flags.Contains(flag);
				}
				else
				{
					return FlagsExtra.ContainsKey(block) && FlagsExtra[block].Contains(flag);
				}
			}

			internal void ToggleFlag(string block, string flag, bool enable)
			{
				List<string> flagList = null;
				if (block == "pragma_surface_shader")
				{
					flagList = Flags;
				}
				else
				{
					if (!FlagsExtra.ContainsKey(block))
					{
						FlagsExtra.Add(block, new List<string>());
					}
					flagList = FlagsExtra[block];
				}

				if (!flagList.Contains(flag) && enable)			flagList.Add(flag);
				else if (flagList.Contains(flag) && !enable)	flagList.Remove(flag);
			}

			//--------------------------------------------------------------------------------------------------
			// KEYWORDS

			internal bool HasKeyword(string key)
			{
				return GetKeyword(key) != null;
			}

			internal string GetKeyword(string key)
			{
				if (key == null)
					return null;

				if (!Keywords.ContainsKey(key))
					return null;

				return Keywords[key];
			}

			internal void SetKeyword(string key, string value)
			{
				if (string.IsNullOrEmpty(value))
				{
					if (Keywords.ContainsKey(key))
						Keywords.Remove(key);
				}
				else
				{
					if (Keywords.ContainsKey(key))
						Keywords[key] = value;
					else
						Keywords.Add(key, value);
				}
			}

			internal void RemoveKeyword(string key)
			{
				if (Keywords.ContainsKey(key))
					Keywords.Remove(key);
			}

			//--------------------------------------------------------------------------------------------------
			// SHADER PROPERTIES / CUSTOM MATERIAL PROPERTIES

			void ExpandAllGroups()
			{
				var keys = headersExpanded.Keys.ToArray();
				foreach (var key in keys)
				{
					headersExpanded[key] = true;
				}
			}

			void FoldAllGroups()
			{
				var keys = headersExpanded.Keys.ToArray();
				foreach (var key in keys)
				{
					headersExpanded[key] = false;
				}
			}

			public string getHeadersExpanded()
			{
				string headersFoldout = "";
				foreach (var kvp in headersExpanded)
				{
					if (kvp.Value)
					{
						headersFoldout += kvp.Key + ",";
					}
				}
				return headersFoldout.TrimEnd(',');
			}

			public void setHeadersExpanded(string expandedHeaders)
			{
				var array = expandedHeaders.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
				var keys = headersExpanded.Keys.ToArray();
				foreach (var key in keys)
				{
					headersExpanded[key] = Array.Exists(array, str => str == key);
				}
			}

			public string getShaderPropertiesExpanded()
			{
				string spExpanded = "";
				foreach (var sp in IterateAllShaderProperties())
				{
					if (sp.expanded)
					{
						spExpanded += sp.Name + ",";
					}
				}
				return spExpanded.TrimEnd(',');
			}

			public void setShaderPropertiesExpanded(string spExpanded)
			{
				var array = spExpanded.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
				foreach (var sp in IterateAllShaderProperties())
				{
					sp.expanded = Array.Exists(array, str => str == sp.Name);
				}
			}

			internal void ShaderPropertiesGUI()
			{
				GUILayout.Space(6);

				GUILayout.BeginHorizontal();

				// Expand / Fold All
				if (GUILayout.Button(TCP2_GUI.TempContent(" Expand All "), EditorStyles.miniButtonLeft))
				{
					ExpandAllGroups();
				}

				if (GUILayout.Button(TCP2_GUI.TempContent(" Fold All "), EditorStyles.miniButtonRight))
				{
					FoldAllGroups();
				}

				GUILayout.FlexibleSpace();

				// Reset All
				bool canReset = false;
				foreach (var sp in cachedShaderProperties)
				{
					if (sp.manuallyModified)
					{
						canReset = true;
						break;
					}
				}
				using (new EditorGUI.DisabledScope(!canReset))
				{
					if (GUILayout.Button(TCP2_GUI.TempContent(" Reset All "), EditorStyles.miniButton))
					{
						if (EditorUtility.DisplayDialog("Reset All Shader Properties", "All Custom Shader Properties will be cleared!\nThis can't be undone!\nProceed?", "Yes", "No"))
						{
							foreach (var sp in cachedShaderProperties)
							{
								sp.ResetDefaultImplementation();
							}
						}
					}
				}
				GUILayout.EndHorizontal();
				GUILayout.Space(4);
				if (ShaderGenerator2.ContextualHelpBox(
					"This section allows you to modify some shader properties that will be used in the shader, based on the features enabled in the corresponding tab.\nClick here to open the documentation and see some examples.",
					"shaderproperties"))
				{
					GUILayout.Space(4);
				}

				if (visibleShaderProperties.Count == 0)
				{
					EditorGUILayout.HelpBox("There are no shader properties for this template.", MessageType.Info);
				}
				else
				{
					for (int i = 0; i < shaderPropertiesUIGroups.Count; i++)
					{
						var group = shaderPropertiesUIGroups[i];

						if (group.header != null)
						{
							EditorGUI.BeginChangeCheck();

							// hover rect as in 2019.3 UI
							var rect = GUILayoutUtility.GetRect(group.header, EditorStyles.foldout, GUILayout.ExpandWidth(true));
							TCP2_GUI.DrawHoverRect(rect);
							rect.xMin += 4; // small left padding
							headersExpanded[group.header.text] = TCP2_GUI.HeaderFoldoutHighlightErrorGrayPosition(rect, headersExpanded[group.header.text], group.header, group.hasErrors, group.hasModifiedShaderProperties);

							if (EditorGUI.EndChangeCheck())
							{
								// expand/fold all when alt/control is held
								if (Event.current.alt || Event.current.control)
								{
									if (headersExpanded[group.header.text])
									{
										ExpandAllGroups();
									}
									else
									{
										FoldAllGroups();
									}
								}
							}
						}

						if (group.header == null || headersExpanded[group.header.text])
						{
							foreach (var sp in group.shaderProperties)
							{
								sp.ShowGUILayout(14);
							}
						}
					}
				}

				// Custom Material Properties
				if (visibleShaderProperties.Count > 0)
				{
					CustomMaterialPropertiesGUI();
				}
			}

			// Material Layers UI
			float tabOffsets;
			float tabOffsetsTarget;
			int selected;
			internal void MaterialLayersGUI(out bool shaderPropertiesChange)
			{
				bool spChange = false;
				
				//button callbacks
				ShaderProperty.CustomMaterialProperty.ButtonClick onAdd = index =>
				{
					materialLayers.Add(new MaterialLayer());
					_materialLayersNames = null;
					spChange = true;
				};
				ShaderProperty.CustomMaterialProperty.ButtonClick onRemove = index =>
				{
					foreach (var shaderProperty in cachedShaderProperties)
					{
						if (shaderProperty.linkedMaterialLayers.Contains(materialLayers[index].uid))
						{
							shaderProperty.RemoveMaterialLayer(materialLayers[index].uid);
						}
					}
					materialLayers[index].sourceShaderProperty.WillBeRemoved();
					
					materialLayers.RemoveAt(index);
					_materialLayersNames = null;
					spChange = true;
				};
				
				//draw element callback
				Action<int, float> DrawMaterialLayer = (index, margin) =>
				{
					var matLayer = materialLayers[index];
					EditorGUILayout.BeginVertical(EditorStyles.helpBox);
					{
						using (new SGUILayout.IndentedLine(margin))
						{
							// Header
							const float buttonWidth = 20;
							var rect = EditorGUILayout.GetControlRect(GUILayout.Height(EditorGUIUtility.singleLineHeight));
							rect.width -= buttonWidth * 2;

							TCP2_GUI.DrawHoverRect(rect);
							
							EditorGUI.BeginChangeCheck();
							matLayer.expanded = GUI.Toggle(rect, matLayer.expanded, TCP2_GUI.TempContent("Layer: " + matLayer.name), TCP2_GUI.HeaderDropDown);
							if (EditorGUI.EndChangeCheck())
							{
								if (Event.current.alt || Event.current.control)
								{
									var state = matLayer.expanded;
									foreach (var ml in materialLayers)
									{
										ml.expanded = state;
									}
								}
							}

							// Add/Remove buttons
							rect.x += rect.width;
							rect.width = buttonWidth;
							rect.height = EditorGUIUtility.singleLineHeight;
							if (GUI.Button(rect, "+", EditorStyles.miniButtonLeft))
							{
								onAdd(index);
							}

							rect.x += rect.width;
							if (GUI.Button(rect, "-", EditorStyles.miniButtonRight))
							{
								onRemove(index);
							}
						}

						// Parameters:
						if (matLayer.expanded)
						{

							using (new SGUILayout.IndentedLine(margin))
							{
								matLayer.name = EditorGUILayout.DelayedTextField(TCP2_GUI.TempContent("Name"), matLayer.name);
							}

							using (new SGUILayout.IndentedLine(margin))
							{
								GUILayout.Label(TCP2_GUI.TempContent("ID"), GUILayout.Width(EditorGUIUtility.labelWidth - 4));
								using (new EditorGUI.DisabledScope(true))
								{
									EditorGUILayout.TextField(GUIContent.none, matLayer.uid);
								}
							}

							using (new SGUILayout.IndentedLine(margin))
							{
								using (new EditorGUI.DisabledScope(true))
								{
									GUILayout.Label("The ID will be replaced with the actual Material Layer name for variables and labels in the final shader.", SGUILayout.Styles.GrayMiniLabelWrap);
								}
							}

							using (new SGUILayout.IndentedLine(margin))
							{
								EditorGUI.BeginChangeCheck();
								matLayer.UseContrastProperty = EditorGUILayout.Toggle(TCP2_GUI.TempContent("Add Contrast Property", "Automatically add a range property to adjust the layer contrast in the material inspector"), matLayer.UseContrastProperty);
							}

							using (new SGUILayout.IndentedLine(margin))
							{
								matLayer.UseNoiseProperty = EditorGUILayout.Toggle(TCP2_GUI.TempContent("Add Noise Property", "Automatically add a properties to adjust the layer based on a noise texture"), matLayer.UseNoiseProperty);
								if (EditorGUI.EndChangeCheck())
								{
									spChange = true;
								}

								if (GUILayout.Button(TCP2_GUI.TempContent("Load Source Preset "), EditorStyles.miniPullDown, GUILayout.ExpandWidth(false)))
								{
									matLayer.ShowPresetsMenu();
								}
							}

							matLayer.sourceShaderProperty.ShowGUILayout(margin);
							if (matLayer.UseContrastProperty)
							{
								matLayer.contrastProperty.ShowGUILayout(margin);
							}

							if (matLayer.UseNoiseProperty)
							{
								matLayer.noiseProperty.ShowGUILayout(margin);
							}
						}
					}
					EditorGUILayout.EndVertical();
				};

				if (materialLayers.Count == 0)
				{
					if (TCP2_GUI.HelpBoxWithButton("No Material Layers defined.", "Add", 48))
					{
						materialLayers.Add(new MaterialLayer());
						_materialLayersNames = null;
						spChange = true;
					}	
				}
				else
				{
					matLayersLayoutList.DoLayoutList(DrawMaterialLayer, materialLayers, new RectOffset(2, 0, 0, 2));
					
					CustomMaterialPropertiesGUI();
				}

				shaderPropertiesChange = spChange;
			}
			
			void CustomMaterialPropertiesGUI()
			{
				GUILayout.Space(4);
				TCP2_GUI.SeparatorSimple();
				GUILayout.Label("Custom Material Properties", EditorStyles.boldLabel);
				GUILayout.Space(2);
				if (ShaderGenerator2.ContextualHelpBox(
					"You can define your own material properties here, that can then be shared between multiple Shader Properties. For example, this can allow you to pack textures however you want, having a mask for each R,G,B,A channel.",
					"custommaterialproperties"))
				{
					GUILayout.Space(4);
				}

				if (customMaterialPropertiesList == null || customMaterialPropertiesList.Count == 0)
				{
					if (TCP2_GUI.HelpBoxWithButton("No custom material properties defined.", "Add", 48))
					{
						ShowCustomMaterialPropertyMenu(0);
					}
				}
				else
				{
					//button callbacks
					ShaderProperty.CustomMaterialProperty.ButtonClick onAdd = index => ShowCustomMaterialPropertyMenu(index);
					ShaderProperty.CustomMaterialProperty.ButtonClick onRemove = index =>
					{
						customMaterialPropertiesList[index].WillBeRemoved();
						customMaterialPropertiesList.RemoveAt(index);
					};

					//draw element callback
					Action<int, float> DrawCustomTextureItem = (index, margin) =>
					{
						customMaterialPropertiesList[index].ShowGUILayout(index, onAdd, onRemove);
					};

					customTexturesLayoutList.DoLayoutList(DrawCustomTextureItem, customMaterialPropertiesList, new RectOffset(2, 0, 0, 2));
				}
			}

			void ShowCustomMaterialPropertyMenu(int index)
			{
				var menu = new GenericMenu();
				var impType = typeof(ShaderProperty.Imp_MaterialProperty);
				var subTypes = impType.Assembly.GetTypes().Where(type => type.IsSubclassOf(impType));
				foreach (var type in subTypes)
				{
					var menuLabel = type.GetProperty("MenuLabel", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
					string label = (string)menuLabel.GetValue(null, null);
					label = label.Replace("Material Property/", "");
					menu.AddItem(new GUIContent(label), false, OnAddCustomMaterialProperty, new object[] { index, type });
				}
				menu.ShowAsContext();
			}

			void OnAddCustomMaterialProperty(object data)
			{
				var array = (object[])data;
				int index = (int)array[0];
				var type = (Type)array[1];

				if (customMaterialPropertiesList.Count == 0)
				{
					customMaterialPropertiesList.Add(CreateUniqueCustomTexture(type));
				}
				else
				{
					customMaterialPropertiesList.Insert(index + 1, CreateUniqueCustomTexture(type));
				}

				ShaderGenerator2.PushUndoState();
			}

			//Get a Shader Property from the list by its name
			internal ShaderProperty GetShaderPropertyByName(string name)
			{
				foreach (var sp in visibleShaderProperties)
					if (sp.Name == name)
						return sp;

				return null;
			}

			//Check if the supplied property name is unique
			internal bool IsUniquePropertyName(string name, IMaterialPropertyName propertyName)
			{
				//check existing Shader Properties of Material Property type
				foreach (var sp in visibleShaderProperties)
				{
					foreach (var imp in sp.implementations)
					{
						var mp = imp as ShaderProperty.Imp_MaterialProperty;
						if (mp != null && mp is IMaterialPropertyName && mp != propertyName && !mp.ignoreUniquePropertyName && mp.PropertyName == name)
						{
							return false;
						}
					}
				}

				//check Custom Material Properties
				foreach (var ct in customMaterialPropertiesList)
				{
					if (ct != propertyName && ct.PropertyName == name)
					{
						return false;
					}
				}

				return true;
			}

			ShaderProperty.CustomMaterialProperty CreateUniqueCustomTexture(Type impType)
			{
				return new ShaderProperty.CustomMaterialProperty(this.customMaterialPropertyShaderProperty, impType);
			}

			internal void ClearShaderProperties()
			{
				this.cachedShaderProperties.Clear();
				this.visibleShaderProperties.Clear();
			}

			//Update available Shader Properties based on conditions
			internal void UpdateShaderProperties(Template template)
			{
				//Add Unity versions to features
#if UNITY_5_4_OR_NEWER
				Utils.AddIfMissing(Features, "UNITY_5_4");
#endif
#if UNITY_5_5_OR_NEWER
				Utils.AddIfMissing(Features, "UNITY_5_5");
#endif
#if UNITY_5_6_OR_NEWER
				Utils.AddIfMissing(Features, "UNITY_5_6");
#endif
#if UNITY_2017_1_OR_NEWER
				Utils.AddIfMissing(this.Features, "UNITY_2017_1");
#endif
#if UNITY_2018_1_OR_NEWER
				Utils.AddIfMissing(this.Features, "UNITY_2018_1");
#endif
#if UNITY_2018_2_OR_NEWER
				Utils.AddIfMissing(this.Features, "UNITY_2018_2");
#endif
#if UNITY_2018_3_OR_NEWER
				Utils.AddIfMissing(this.Features, "UNITY_2018_3");
#endif
#if UNITY_2019_1_OR_NEWER
				Utils.AddIfMissing(this.Features, "UNITY_2019_1");
#endif
#if UNITY_2019_2_OR_NEWER
				Utils.AddIfMissing(this.Features, "UNITY_2019_2");
#endif
#if UNITY_2019_3_OR_NEWER
				Utils.AddIfMissing(this.Features, "UNITY_2019_3");
#endif
#if UNITY_2019_4_OR_NEWER
				Utils.AddIfMissing(this.Features, "UNITY_2019_4");
#endif
#if UNITY_2020_1_OR_NEWER
				Utils.AddIfMissing(this.Features, "UNITY_2020_1");
#endif
#if UNITY_2021_1_OR_NEWER
				Utils.AddIfMissing(this.Features, "UNITY_2021_1");
#endif
#if UNITY_2021_2_OR_NEWER
				Utils.AddIfMissing(this.Features, "UNITY_2021_2");
#endif
#if UNITY_2022_2_OR_NEWER
				Utils.AddIfMissing(this.Features, "UNITY_2022_2");
#endif
#if UNITY_6000_0_OR_NEWER
				Utils.AddIfMissing(this.Features, "UNITY_6000_2");
#endif
#if UNITY_6000_1_OR_NEWER
				Utils.AddIfMissing(this.Features, "UNITY_6000_1");
#endif
#if UNITY_6000_2_OR_NEWER
				Utils.AddIfMissing(this.Features, "UNITY_6000_0");
#endif

				var parsedLines = template.GetParsedLinesFromConditions(this, null, null);

				//Clear arrays: will be refilled with the template's shader properties
				visibleShaderProperties.Clear();
				Dictionary<int, GUIContent> shaderPropertiesHeaders;
				visibleShaderProperties.AddRange(template.GetConditionalShaderProperties(parsedLines, out shaderPropertiesHeaders));
				foreach (var sp in visibleShaderProperties)
				{
					//add to the cached properties, to be found back if needed (in case of features change)
					if (!cachedShaderProperties.Contains(sp))
					{
						cachedShaderProperties.Add(sp);
					}

					if (Features.Contains("OCCLUSION") && sp.Name == "Occlusion")
					{
						ForceDefaultOcclusionTexture(sp);
					}

					// resolve linked shader property references now that all visible shader properties are known
					sp.ResolveShaderPropertyReferences();

					sp.onImplementationsChanged -= onShaderPropertyImplementationsChanged; // lazy way to make sure we don't subscribe more than once
					sp.onImplementationsChanged += onShaderPropertyImplementationsChanged;
				}
				
				// Material Layers
				foreach (var ml in materialLayers)
				{
					visibleShaderProperties.Add(ml.sourceShaderProperty);
					if (ml.contrastProperty != null)
					{
						visibleShaderProperties.Add(ml.contrastProperty);
					}
					if (ml.noiseProperty != null)
					{
						visibleShaderProperties.Add(ml.noiseProperty);
					}
				}

				//Find used shader properties per pass, to extract used features for each
				template.UpdateInjectionPoints(parsedLines);
				shaderPropertiesPerPass = template.FindUsedShaderPropertiesPerPass(parsedLines);

				// Build list of shader properties and headers for the UI
				shaderPropertiesUIGroups.Clear();
				ShaderPropertyGroup currentGroup = new ShaderPropertyGroup()
				{
					shaderProperties = new List<ShaderProperty>(),
					hasModifiedShaderProperties = false,
					hasErrors = false,
					header = null
				};


				Action addCurrentGroup = () =>
				{
					if (currentGroup.shaderProperties.Count > 0)
					{
						shaderPropertiesUIGroups.Add(currentGroup);

						if (!headersExpanded.ContainsKey(currentGroup.header.text))
						{
							headersExpanded.Add(currentGroup.header.text, false);
						}
					}
				};

				for (int i = 0; i < visibleShaderProperties.Count; i++)
				{
					if (shaderPropertiesHeaders.ContainsKey(i))
					{
						addCurrentGroup();

						currentGroup = new ShaderPropertyGroup()
						{
							shaderProperties = new List<ShaderProperty>(),
							hasModifiedShaderProperties = false,
							hasErrors = false,
							header = shaderPropertiesHeaders[i]
						};
					}

					var shaderProperty = visibleShaderProperties[i];
					if (shaderProperty.isMaterialLayerProperty)
					{
						// Don't show Material Layer source in regular Shader Properties
						continue;
					}
					
					currentGroup.shaderProperties.Add(shaderProperty);
					currentGroup.hasModifiedShaderProperties |= shaderProperty.manuallyModified;
					currentGroup.hasErrors |= shaderProperty.error;
				}
				addCurrentGroup();
			}

			static void ForceDefaultOcclusionTexture(ShaderProperty shaderProperty)
			{
				if (shaderProperty == null)
				{
					return;
				}

				var hasAoTexture = shaderProperty.implementations.Count == 1
					&& shaderProperty.implementations[0] is ShaderProperty.Imp_MaterialProperty_Texture texture
					&& texture.PropertyName == "_OcclusionMap"
					&& texture.Label == "AO Texture";

				if (!hasAoTexture)
				{
					shaderProperty.ResetDefaultImplementation();
				}
			}

			public void UpdateCustomMaterialProperties()
			{
				foreach(var cmp in customMaterialPropertiesList)
				{
					cmp.implementation.CheckErrors();
				}
			}

			private void onShaderPropertyImplementationsChanged()
			{
				ShaderGenerator2.NeedsShaderPropertiesUpdate = true;
				ShaderGenerator2.PushUndoState();
			}

			//Process #KEYWORDS line from Template
			//Use temp features & flags to avoid permanent toggles (e.g. NOTILE_SAMPLING)
			//As long as the original features are there, they should be triggered each time anyway
			/// <returns>'true' if a new feature/flag has been added/removed, so that we can reprocess the whole keywords block</returns>
			internal bool ProcessKeywords(string line, List<string> tempFeatures, List<string> tempFlags, Dictionary<string, List<string>> tempExtraFlags)
			{
				if (string.IsNullOrEmpty(line))
				{
					return false;
				}

				//Inside valid block
				var parts = line.Split(new[] { "\t" }, StringSplitOptions.RemoveEmptyEntries);

				// Fixed expressions first:
				switch (parts[0])
				{
					case "set": //legacy
					case "set_keyword":
					{
						var keywordValue = parts.Length > 2 ? parts[2] : "";
						if (Keywords.ContainsKey(parts[1]))
							Keywords[parts[1]] = keywordValue;
						else
							Keywords.Add(parts[1], keywordValue);
						break;
					}

					case "enable_kw": //legacy
					case "feature_on":
					{
						if (Utils.AddIfMissing(tempFeatures, parts[1]))
						{
							return true;
						}

						break;
					}
					case "disable_kw": //legacy
					case "feature_off":
					{
						if (Utils.RemoveIfExists(tempFeatures, parts[1]))
						{
							return true;
						}

						break;
					}

					case "enable_flag": //legacy
					case "flag_on":
						if (tempFlags != null)
						{
							if (Utils.AddIfMissing(tempFlags, parts[1]))
							{
								return true;
							}
						}
						break;
					case "disable_flag": //legacy
					case "flag_off":
						if (tempFlags != null)
						{
							if (Utils.RemoveIfExists(tempFlags, parts[1]))
							{
								return true;
							}
						}
						break;

					default:
					{
						// Dynamic afterwards:
						if (parts[0].StartsWith("flag_on:"))
						{
							if (tempExtraFlags == null)
							{
								return false;
							}

							string block = parts[0].Substring("flag_on:".Length);
							if (!tempExtraFlags.ContainsKey(block)) tempExtraFlags.Add(block, new List<string>());

							if (Utils.AddIfMissing(tempExtraFlags[block], parts[1]))
							{
								return true;
							}
						}
						else if (parts[0].StartsWith("flag_off:"))
						{
							if (tempExtraFlags == null)
							{
								return false;
							}

							string block = parts[0].Substring("flag_on:".Length);
							if (!tempExtraFlags.ContainsKey(block))
							{
								return false;
							}

							if (Utils.RemoveIfExists(tempExtraFlags[block], parts[1]))
							{
								if (tempExtraFlags[block].Count == 0)
								{
									tempExtraFlags.Remove(block);
								}

								return true;
							}
						}
					}
					break;
				}

				return false;
			}

			// Cache the expanded state of the visible shader properties, to restore them after shader generation/update
			static HashSet<string> expandedCache;
			static Dictionary<string, bool> headersExpandedCache;
			void UI_CacheExpandedState()
			{
				headersExpandedCache = new Dictionary<string, bool>();
				foreach (var kvp in headersExpanded)
				{
					headersExpandedCache.Add(kvp.Key, kvp.Value);
				}

				expandedCache = new HashSet<string>();
				foreach (var shaderProperty in visibleShaderProperties)
				{
					if (shaderProperty.expanded)
					{
						expandedCache.Add(shaderProperty.Name);
					}
				}
			}

			void UI_RestoreExpandedState()
			{
				if (expandedCache == null && headersExpandedCache == null)
				{
					return;
				}

				foreach (var kvp in headersExpandedCache)
				{
					if (headersExpanded.ContainsKey(kvp.Key))
					{
						headersExpanded[kvp.Key] = kvp.Value;
					}
					else
					{
						headersExpanded.Add(kvp.Key, kvp.Value);
					}
				}

				foreach (var shaderProperty in visibleShaderProperties)
				{
					if (expandedCache.Contains(shaderProperty.Name))
					{
						shaderProperty.expanded = true;
					}
				}

				expandedCache = null;
				headersExpandedCache = null;
			}

			// Useful callbacks
			public void OnBeforeGenerateShader()
			{
				UI_CacheExpandedState();
			}

			public void OnAfterGenerateShader()
			{
				UI_RestoreExpandedState();
			}
		}
	}
}
