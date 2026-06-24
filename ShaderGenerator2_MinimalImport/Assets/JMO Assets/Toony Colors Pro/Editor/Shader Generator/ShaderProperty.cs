// Toony Colors Pro 2
// (c) 2014-2026 Jean Moreno

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
// Merged from ShaderProperty.cs
// -----------------------------------------------------------------------------

// Represents a user-modifiable shader property, that will be generated and injected in the code.
// It can be defined as a Material Property, Constant, or fetched from another source (e.g. Vertex Color),
// and be combined with other source (e.g. Material Property + Vertex Color * Constant).
// It can also be locked and not modifiable by user, e.g. fixed Material Property.
//
// The Generator will fetch the ShaderProperty list and generate the relevant code for the shader:
// - Properties { } block
// - Variables declaration
// - Variables initialization

namespace ToonyColorsPro
{
	namespace ShaderGenerator
	{
		// Enums that can be used in the templates for fixed function enums
		// They have to be outside of any class to work properly with the
		// enum material property drawers

		public enum BlendFactor
		{
			[Enums.Order(0)]										One					= UnityEngine.Rendering.BlendMode.One,
			[Enums.Order(1)]										Zero				= UnityEngine.Rendering.BlendMode.Zero,
			[Enums.Order(2), Enums.Label("Source Color")]			SrcColor			= UnityEngine.Rendering.BlendMode.SrcColor,
			[Enums.Order(3), Enums.Label("1 - Source Color")]		OneMinusSrcColor	= UnityEngine.Rendering.BlendMode.OneMinusSrcColor,
			[Enums.Order(4), Enums.Label("Destination Color")]		DstColor			= UnityEngine.Rendering.BlendMode.DstColor,
			[Enums.Order(5), Enums.Label("1 - Destination Color")]	OneMinusDstColor	= UnityEngine.Rendering.BlendMode.OneMinusDstColor,
			[Enums.Order(6), Enums.Label("Source Alpha")]			SrcAlpha			= UnityEngine.Rendering.BlendMode.SrcAlpha,
			[Enums.Order(7), Enums.Label("1 - Source Alpha")]		OneMinusSrcAlpha	= UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha,
			[Enums.Order(8), Enums.Label("Destination Alpha")]		DstAlpha			= UnityEngine.Rendering.BlendMode.DstAlpha,
			[Enums.Order(9), Enums.Label("1 - Destination Alpha")]	OneMinusDstAlpha	= UnityEngine.Rendering.BlendMode.OneMinusDstAlpha
		}

		public enum BlendOperation
		{
			[Enums.Order(0)]									Add		= UnityEngine.Rendering.BlendOp.Add,
			[Enums.Order(1), Enums.Label("Subtract")]			Sub		= UnityEngine.Rendering.BlendOp.Subtract,
			[Enums.Order(2), Enums.Label("Reverse Subtract")]	RevSub	= UnityEngine.Rendering.BlendOp.ReverseSubtract,
			[Enums.Order(3)]									Min		= UnityEngine.Rendering.BlendOp.Min,
			[Enums.Order(4)]									Max		= UnityEngine.Rendering.BlendOp.Max
		}

		public enum DepthWrite
		{
			[Enums.Order(0)] On = 1,
			[Enums.Order(1)] Off = 0
		}

		public enum CompareFunction
		{
			[Enums.Order(0)]									Never		= UnityEngine.Rendering.CompareFunction.Never,
			[Enums.Order(1)]									Less		= UnityEngine.Rendering.CompareFunction.Less,
			[Enums.Order(2), Enums.Label("Less or Equal")]		LEqual		= UnityEngine.Rendering.CompareFunction.LessEqual,
			[Enums.Order(3)]									Equal		= UnityEngine.Rendering.CompareFunction.Equal,
			[Enums.Order(4), Enums.Label("Greater or Equal")]	GEqual		= UnityEngine.Rendering.CompareFunction.GreaterEqual,
			[Enums.Order(5)]									Greater		= UnityEngine.Rendering.CompareFunction.Greater,
			[Enums.Order(6), Enums.Label("Not Equal")]			NotEqual	= UnityEngine.Rendering.CompareFunction.NotEqual,
			[Enums.Order(7)]									Always		= UnityEngine.Rendering.CompareFunction.Always
		}

		public enum StencilOperation
		{
			[Enums.Order(0)]										Keep = UnityEngine.Rendering.StencilOp.Keep,
			[Enums.Order(1)]										Zero = UnityEngine.Rendering.StencilOp.Zero,
			[Enums.Order(2)]										Replace = UnityEngine.Rendering.StencilOp.Replace,
			[Enums.Order(3)]										Invert = UnityEngine.Rendering.StencilOp.Invert,
			[Enums.Order(4), Enums.Label("Increment Saturate")]		IncrSat = UnityEngine.Rendering.StencilOp.IncrementSaturate,
			[Enums.Order(5), Enums.Label("Decrement Saturate")]		DecrSat = UnityEngine.Rendering.StencilOp.DecrementSaturate,
			[Enums.Order(6), Enums.Label("Increment Wrap")]			IncrWrap = UnityEngine.Rendering.StencilOp.IncrementWrap,
			[Enums.Order(7), Enums.Label("Decrement Wrap")]			DecrWrap = UnityEngine.Rendering.StencilOp.DecrementWrap,
		}

		public enum Culling
		{
			[Enums.Order(0), Enums.Label("Back faces")]			Back	= UnityEngine.Rendering.CullMode.Back,
			[Enums.Order(1), Enums.Label("Front faces")]		Front	= UnityEngine.Rendering.CullMode.Front,
			[Enums.Order(2), Enums.Label("Off (double-sided)")]	Off		= UnityEngine.Rendering.CullMode.Off
		}

		/// <summary>
		/// User-friendly enum value names system
		/// </summary>
		public class Enums
		{
			[AttributeUsage(AttributeTargets.Field)]
			public class Label : Attribute
			{
				public string label;

				public Label(string label)
				{
					this.label = label;
				}
			}

			[AttributeUsage(AttributeTargets.Field)]
			public class Order : Attribute
			{
				public int order;

				public Order(int order)
				{
					this.order = order;
				}
			}

			/// <summary>
			/// Returns an array of the enum values, sorted by their [Order] attribute.
			/// This allows the custom enums to be in any order, retaining the original values that correspond to the built-in Unity enum.
			/// </summary>
			static public OrderedEnum[] GetOrderedEnumValues(Type enumType)
			{
				if(!enumType.IsEnum)
				{
					Debug.LogError("Not an enum type: " + enumType);
					return null;
				}

				List<OrderedEnum> orderedEnums = new List<OrderedEnum>();
				var fields = enumType.GetFields();
				foreach (var field in fields)
				{
					var orders = (Order[])field.GetCustomAttributes(typeof(Order), false);
					var labels = (Label[])field.GetCustomAttributes(typeof(Label), false);
					if (orders != null && orders.Length > 0)
					{
						Enum value = (Enum)field.GetValue(null);
						string name = value.ToString();
						if(labels != null && labels.Length > 0)
						{
							name = labels[0].label;
						}

						orderedEnums.Add(new OrderedEnum()
						{
							value = value,
							order = orders[0].order,
							displayName = name
						});
					}
				}
				orderedEnums.Sort((x,y) => x.order.CompareTo(y.order));
				return orderedEnums.ToArray();
			}

			public struct OrderedEnum
			{
				public Enum value;
				public string displayName;
				public int order;
			}
		}

		[Serialization.SerializeAs("sp", "manuallyModified")]
		public partial class ShaderProperty
		{
			static class UI
			{
				public const float GUI_NEWLINE_INDENT = 20;
				public const float GUI_RIGHT_BUTTONS = 40;
			}

			public enum ProgramType
			{
				Undefined,
				Vertex,
				Fragment,
				FixedFunction
			}

			internal enum OptionFeatures
			{
				VertexColors,
				NoTile_Sampling,
				NoTile_Sampling_Vertex,
				Triplanar_Sampling,
				Triplanar_Sampling_Vertex,
				Triplanar_Sampling_Global,
				Triplanar_Sampling_Local,
				HSV_Full,
				HSV_Grayscale,
				HSV_Colorize,
				Screen_Space_UV_Vertex,
				Screen_Space_UV_Fragment,
				Screen_Space_UV_Object_Offset,
				UV_Anim_Random_Offset,
				UV_Anim_Sine,
				UV_Anim_Sine_World,
				Scale_By_Texel_Size,
				World_Pos_UV_Fragment,
				World_Pos_UV_Vertex,
				Local_Pos_Fragment,
				Local_Normal_Fragment,
				World_Normal_Vertex,
				World_Normal_Fragment
			}

			[Flags]
			public enum VariableType
			{
				@float = 1,
				float2 = 2,
				float3 = 4,
				float4 = 8,
				color = 16,
				color_rgba = 32,
				fixed_function_float = 64,
				fixed_function_enum = 128,
			}

			internal static bool CheckVariableType(VariableType set, VariableType element)
			{
				return (set & element) == element;
			}

			internal static bool VariableTypeIsFixedFunction(VariableType type)
			{
				return type == VariableType.fixed_function_float || type == VariableType.fixed_function_enum;
			}

			// Doesn't include 'fixed_function' as it is a special type
			const VariableType VariableTypeAll = VariableType.@float | VariableType.float2 | VariableType.float3 | VariableType.float4 | VariableType.color | VariableType.color_rgba;

			static string VariableTypeToShaderCode(VariableType type)
			{
				//TODO Handle float precision maybe?
				switch (type)
				{
					case VariableType.color:
					case VariableType.float3:
						return "float3";
					case VariableType.color_rgba:
					case VariableType.float4:
						return "float4";
					case VariableType.@float:
						return "float";
					case VariableType.float2:
						return "float2";
				}

				return null;
			}

			string VariableTypeToName(VariableType type)
			{
				if (type == VariableType.color_rgba)
				{
					return "color (rgba)";
				}
				else if (type == VariableType.fixed_function_float)
				{
					return "float (fixed function)";
				}
				else if (type == VariableType.fixed_function_enum)
				{
					return "enum (fixed function)";
				}
				else
				{
					return type.ToString();
				}
			}

			public static int VariableTypeToChannelsCount(VariableType type)
			{
				switch (type)
				{
					case VariableType.color:
					case VariableType.float3:
						return 3;

					case VariableType.color_rgba:
					case VariableType.float4:
						return 4;

					case VariableType.@float:
						return 1;

					case VariableType.float2:
						return 2;
				}

				return -1;
			}

			public enum FloatPrecision
			{
				@float,
				half,
				@fixed
			}

			public enum ColorPrecision
			{
				LDR,
				HDR
			}

			public enum Operator
			{
				Multiply,
				Divide,
				Add,
				Subtract
			}

			static string[] OperatorSymbols = { "×", "÷", "+", "-" };

			//================================================================================================================================

			//Needed so that we can instantiate using System.Activator with ShaderProperty argument (when deserialiazing a Imp_ShaderPropertyReference):
			//a new ShaderProperty will be created, just so that we can retrieve its name, and find the correct existing one in the list (the one created is then destroyed)
			public ShaderProperty(ShaderProperty sp) { }
			public ShaderProperty() { }

			[Serialization.CustomDeserializeCallback]
			static ShaderProperty Deserialize(string data, object[] args)
			{
				var shaderProperty = new ShaderProperty();
				
				// custom callback for Implementations
				Func<object, string, object> onDeserializeImplementation = (impObj, impData) =>
				{
					return ShaderGenerator2.CurrentConfig.DeserializeImplementationHandler(impObj, impData, shaderProperty);
				};
				var implementationHandling = new Dictionary<Type, Func<object, string, object>> { { typeof(ShaderProperty.Implementation), onDeserializeImplementation } };
                    
				return (ShaderProperty)Serialization.DeserializeTo(shaderProperty, data, typeof(ShaderProperty), null, implementationHandling);
			}
			
			//================================================================================================================================

			ReorderableLayoutList layoutList = new ReorderableLayoutList();

			public string Name { get { return _name; } private set { _name = value; } }
			[Serialization.SerializeAs("name")] string _name;
			[Serialization.SerializeAs("imps")] public List<Implementation> implementations;
			[Serialization.SerializeAs("layers")] public List<string> linkedMaterialLayers = new List<string>();
			[Serialization.SerializeAs("unlocked")] public List<string> unlockedMaterialLayers = new List<string>();
			[Serialization.SerializeAs("layer_blend")] public Dictionary<string, MaterialLayer.BlendType> materialLayerBlendings = new Dictionary<string, MaterialLayer.BlendType>();
			[Serialization.SerializeAs("custom_blend")] public Dictionary<string, string> materialLayercustomBlendings = new Dictionary<string, string>();
			[Serialization.SerializeAs("clones"), Serialization.ForceSerialization] public Dictionary<string, ShaderProperty> clonedShaderProperties = new Dictionary<string, ShaderProperty>();

			internal const string DefaultCustomBlending = "lerp(a, b, s)";

			public VariableType Type { get; private set; }
			public ProgramType Program = ProgramType.Undefined;
			public bool IsUsedInLightingFunction = false;   //TODO same process for IsUsedInVertexFunction for vert/frag shaders and automatic float4 texcoordN packing
			readonly List<int> usedImplementationsForCustomCode = new List<int>();

			// Material Layers
			[Serialization.SerializeAs("isClone")] internal bool isLayerClone = false;
			internal bool isMaterialLayerProperty { get { return materialLayerUid != null; } }
			internal string materialLayerUid = null;
			string layerCloneSuffix = null;
			string layersTooltip = null;

			int passBitmask;    //bitmask that determines in which passes the shader property is used
			Implementation[] defaultImplementations;
			public bool expanded;
			readonly List<int> implementationsExpandedStates = new List<int>();
			string helpMessage;
			string displayName = null;
			public string DisplayName
			{
				get
				{
					if (!string.IsNullOrEmpty(displayName))
					{
						if (isMaterialLayerProperty)
						{
							var ml = ShaderGenerator2.CurrentConfig.GetMaterialLayerByUID(materialLayerUid);
							if (ml != null)
							{
								return displayName.Replace(materialLayerUid, ml.name);
							}
						}

						return displayName;
					}

					return this.Name;
				}
				set { displayName = value; }
			}

			public delegate void OnImplementationsChanged();
			public OnImplementationsChanged onImplementationsChanged;

			int defaultImplementationHash = 0;
			public bool manuallyModified { get; private set; }
			public bool error { get; private set; }
			// indicates whether this property should be sampled when using its value, or at the beginning of the vert/frag functions
			public bool deferredSampling { get; set; }
			public bool cantReferenceOtherProperties { get; private set; }
			public string preventReference { get; private set; }

			public bool isHook = false;
			public string toggleFeatures = null;

			//================================================================================================================================

			public ShaderProperty(string name, VariableType type)
			{
				Name = name;
				Type = type;
				implementations = new List<Implementation> { new Imp_ConstantValue(this) };
				CallOnImplementationsChanged();
				CheckErrors();

				CustomMaterialProperty.OnCustomMaterialPropertyRemoved += OnCustomTextureRemoved;
			}

			public ShaderProperty CloneForLayer(MaterialLayer materialLayer)
			{
				var clone = new ShaderProperty();
				clone.isLayerClone = true;
				clone.Name = this.Name + "_" + materialLayer.uid;
				clone.Type = this.Type;
				clone.passBitmask = this.passBitmask;
				clone.implementations = new List<Implementation>();
				clone.SetDefaultImplementations(this.defaultImplementations);
				clone.implementations.Clear();
				foreach (var imp in implementations)
				{
					var clonedImp = imp.CloneForNewShaderProperty(clone, materialLayer.uid);
					clone.implementations.Add(clonedImp);
				}
				clone.CallOnImplementationsChanged();
				clone.CheckErrors();
				clone.CheckHash();
				return clone;
			}

			internal IEnumerable<ShaderProperty> IterateUsedClonedProperties()
			{
				foreach (var uid in linkedMaterialLayers)
				{
					if (unlockedMaterialLayers.Contains(uid))
					{
						// print the cloned Shader Property related to this layer
						yield return clonedShaderProperties[uid];
					}
				}
			}
			
			/// Note: can return the same CustomMaterialProperty more than once
			internal IEnumerable<CustomMaterialProperty> IterateCustomMaterialProperties()
			{
				var alreadyYielded = new HashSet<CustomMaterialProperty>();
				foreach (var imp in implementations)
				{
					var imp_cmp = imp as Imp_CustomMaterialProperty;
					if (imp_cmp != null && imp_cmp.LinkedCustomMaterialProperty != null)
					{
						yield return imp_cmp.LinkedCustomMaterialProperty;
					}
				}
			}

			internal void WillBeRemoved()
			{
				foreach (var imp in implementations)
				{
					imp.WillBeRemoved();
				}
			}

			void OnCustomTextureRemoved(CustomMaterialProperty ct)
			{
				// expand this Shader Property if a linked Custom Material Property was removed to show the message
				foreach (var imp in this.implementations)
				{
					var imp_ct = imp as Imp_CustomMaterialProperty;
					if (imp_ct != null && imp_ct.LinkedCustomMaterialProperty == ct)
					{
						imp_ct.LinkedCustomMaterialProperty = null;
					}

					var imp_mp_tex = imp as Imp_MaterialProperty_Texture;
					if (imp_mp_tex != null
					    && imp_mp_tex.UvSource == ShaderProperty.Imp_MaterialProperty_Texture.UvSourceType.CustomMaterialProperty
					    && imp_mp_tex.LinkedCustomMaterialProperty == ct)
					{
						imp_mp_tex.LinkedCustomMaterialProperty = null;
					}
				}

				CallOnImplementationsChanged();
				CheckErrors();
			}

			[Serialization.OnDeserializeCallback]
			void OnDeserialize()
			{
				UpdateLayersTooltip();
				
				if (clonedShaderProperties.Count > 0)
				{
					foreach (var clonedShaderProperty in clonedShaderProperties.Values)
					{
						// non-serialized fields shared with the clones' source:
						clonedShaderProperty.Type = this.Type;
						clonedShaderProperty.Program = this.Program;

						// clones should share the same default implementations as their source
						clonedShaderProperty.defaultImplementations = this.defaultImplementations;
					}
				}
				
				CallOnImplementationsChanged();
			}

			void CallOnImplementationsChanged()
			{
				if (onImplementationsChanged != null)
				{
					onImplementationsChanged();
				}
			}

			public override string ToString()
			{
				return string.Format("[Shader Property: {0}]", Name);
			}

			public void AddPassUsage(int pass)
			{
				passBitmask |= 1 << pass;
			}

			public void SetDefaultImplementations(params Implementation[] imps)
			{
				defaultImplementations = imps;
				ResetDefaultImplementation();
			}

			int GetImplementationsHash()
			{
				string hash = "";
				foreach (var imp in implementations)
				{
					hash += imp.ToHashString();
				}
				return hash.GetHashCode();
			}

			public void ResetDefaultImplementation(bool clearMaterialLayers = true)
			{
				foreach (var imp in implementations)
				{
					imp.WillBeRemoved();
				}

				implementations.Clear();
				foreach (var imp in defaultImplementations)
				{
					implementations.Add(imp.Clone());
				}

				if (clearMaterialLayers)
				{
					linkedMaterialLayers.Clear();
					materialLayerBlendings.Clear();
					materialLayercustomBlendings.Clear();
					unlockedMaterialLayers.Clear();
					clonedShaderProperties.Clear();
				}

				ResolveShaderPropertyReferences();

				defaultImplementationHash = GetImplementationsHash();
				CallOnImplementationsChanged();
				CheckErrors();
				CheckHash();
			}

			public bool IsImplementationUsedInCustomCode(Implementation imp)
			{
				int index = this.implementations.IndexOf(imp);
				if (index < 0)
				{
					Debug.LogWarning($"Implementation '{imp.Label}' is not used in this Shader Property '{this.Name}'");
					return false;
				}
				return this.usedImplementationsForCustomCode.Contains(index);
			}

			public void ForceUpdateDefaultHash()
			{
				defaultImplementationHash = GetImplementationsHash();
			}

			void OnResetImplementation(object resetMaterialLayers)
			{
				ResetDefaultImplementation(resetMaterialLayers != null);
				ShaderGenerator2.NeedsShaderPropertiesUpdate = true;
			}

			public void CheckErrors()
			{
				bool wasError = this.error;
				this.error = false;
				foreach (var imp in implementations)
				{
					if (imp == null)
					{
						continue;
					}

					imp.CheckErrors();
					this.error |= imp.HasErrors;
				}

				if (wasError != error)
				{
					//ShaderGenerator2.NeedsShaderPropertiesUpdate = true;
				}
			}

			public void CheckHash()
			{
				int newHash = GetImplementationsHash();
				manuallyModified = defaultImplementationHash != newHash;
				manuallyModified |= linkedMaterialLayers.Count > 0;
				ShaderGenerator2.NeedsShaderPropertiesUpdate = true;
			}

			/// <summary>
			/// Is the Shader Property currently visible in this Config?
			/// </summary>
			public bool IsVisible()
			{
				if (ShaderGenerator2.CurrentConfig == null) return false;

				return Array.Exists(ShaderGenerator2.CurrentConfig.VisibleShaderProperties, sp => sp == this);
			}
			
			void UpdateLayersTooltip()
			{
				layersTooltip = "";
				foreach (string uid in linkedMaterialLayers)
				{
					layersTooltip += string.Format("\n- {0}", ShaderGenerator2.CurrentConfig.GetMaterialLayerByUID(uid).name);
				}
			}

			internal void AddMaterialLayer(string uid)
			{
				this.linkedMaterialLayers.Add(uid);
				if (!materialLayerBlendings.ContainsKey(uid))
				{
					this.materialLayerBlendings.Add(uid, MaterialLayer.BlendType.LinearInterpolation);
					this.materialLayercustomBlendings.Add(uid, DefaultCustomBlending);
				}
				UpdateLayersTooltip();
			}

			internal void RemoveMaterialLayer(string uid)
			{
				this.linkedMaterialLayers.Remove(uid);
				UpdateLayersTooltip();
			}

			string CallMethodWithCloneSuffixForEachLayer(Func<ShaderProperty, string> callback)
			{
				string output = "";
				foreach (var uid in linkedMaterialLayers)
				{
					if (unlockedMaterialLayers.Contains(uid))
					{
						// print the cloned Shader Property related to this layer
						output += callback(clonedShaderProperties[uid]);
					}
					else
					{
						// clone this Shader Property with suffix
						this.layerCloneSuffix = uid;
						output += callback(this);	
					}
				}
				this.layerCloneSuffix = null;
				return output;
			}

			string CallMethodWithCloneSuffixForLayer(string uid, Func<ShaderProperty, string> callback)
			{
				if (unlockedMaterialLayers.Contains(uid))
				{
					return callback(clonedShaderProperties[uid]);
				}
				else
				{
					this.layerCloneSuffix = uid;
					string output = callback(this);
					this.layerCloneSuffix = null;
					return output;
				}
			}

			//Print the properties from this ShaderProperty, if any
			public string PrintProperties(string indent = "")
			{
				var result = "";
				foreach (var i in implementations)
				{
					var str = i.PrintProperty(indent);
					if (!string.IsNullOrEmpty(str))
					{
						result += indent + str + "\n";
					}
				}
				if (string.IsNullOrEmpty(result.Trim()))
					return "";
				return result.TrimEnd('\n').TrimStart();
			}

			internal string PrintPropertiesForLayer(string indent, string uid)
			{
				string output = "";
				if (linkedMaterialLayers.Contains(uid))
				{
					output += CallMethodWithCloneSuffixForLayer(uid, (sp) => string.Format("\n{0}{1}", indent, sp.PrintProperties(indent)));
				}
				return output;
			}

			//Print the variables/properties declaration for this ShaderProperty, if any
			public string PrintVariableDeclare(bool gpuInstanced, string indent)
			{
				string output = PrintVariableDeclare_Internal(indent, gpuInstanced, false);
				output += CallMethodWithCloneSuffixForEachLayer((sp) => string.Format("\n{0}", sp.PrintVariableDeclare_Internal(indent, gpuInstanced, false)));
				return output;
			}
			
			public List<string> PrintVariablesDeclareDotsInstancing()
			{
				var list = new List<string>();
				list.Add(PrintVariableDeclare_Internal("", false, true));
				CallMethodWithCloneSuffixForEachLayer((sp) =>
				{
					list.Add(sp.PrintVariableDeclare_Internal("", false, true));
					return null;
				});
				return list;
			}

			public string PrintVariableDeclare_Internal(string indent, bool gpuInstanced, bool dotsInstanced)
			{
				string result = "";
				foreach (Implementation imp in implementations)
				{
					if (dotsInstanced && !imp.IsDotsInstanced) continue;

					string str = imp.PrintVariableDeclare(indent, gpuInstanced);
					if (!string.IsNullOrEmpty(str))
					{
						result += str + "\n";
					}
				}

				if (string.IsNullOrEmpty(result.Trim()))
				{
					return "";
				}

				return result.TrimEnd('\n');
			}

			//Print the variables/properties declaration that are incompatible with CBuffer/GPU instancing buffer
			public string PrintVariableDeclareOutsideCBuffer(string indent)
			{
				string output = PrintVariableDeclareOutsideCBuffer_Internal(indent);
				output += CallMethodWithCloneSuffixForEachLayer((sp) => string.Format("\n{0}", sp.PrintVariableDeclareOutsideCBuffer_Internal(indent)));
				return output;
			}
			
			public string PrintVariableDeclareOutsideCBuffer_Internal(string indent)
			{
				string result = "";
				foreach (var imp in implementations)
				{
					string prop = imp.PrintVariableDeclareOutsideCBuffer(indent);
					if (prop != null)
					{
						result += prop + "\n";
					}
				}
				return result.TrimEnd('\n');
			}

			//Print variables in SurfaceOutput so that they can be used in the Lighting function (and possibly cross-referenced in the Surface function)
			public string PrintVariableSurfaceOutput(string indent = "")
			{
				if (!IsUsedInLightingFunction || deferredSampling)
					return "";

				return string.Format("{0} {1};", VariableTypeToShaderCode(Type), GetVariableName());
			}

			//Print the variable(s) sampling/calculations for this ShaderProperty
			public string PrintVariableSample(string inputSource, string outputSource, ProgramType program, string arguments, string indent, string prefix = null, bool skipBaseProperty = false)
			{
				string output = skipBaseProperty ? "" : PrintVariableSample_Internal(inputSource, outputSource, program, arguments, prefix);
				output += CallMethodWithCloneSuffixForEachLayer((sp) => string.Format("\n{0}{1}", indent, sp.PrintVariableSample_Internal(inputSource, outputSource, program, arguments, prefix)));
				return output;
			}
			
			string PrintVariableSample_Internal(string inputSource, string outputSource, ProgramType program, string arguments, string prefix = null)
			{
				return PrintVariableSample(inputSource, outputSource, program, arguments, true, prefix);
			}
			
			private string PrintVariableSample(string inputSource, string outputSource, ProgramType program, string arguments, bool declareVariable, string prefix = null)
			{
				var result = "";
				HashSet<Implementation> usedImplementations = new HashSet<Implementation>(); //some implementations can be used by custom code
				for (var i = 0; i < implementations.Count; i++)
				{
					var imp = implementations[i];

					var imp_cc = imp as Imp_CustomCode;
					var imp_hsv = imp as Imp_HSV;
					if (imp_cc != null && imp_cc.usesReplacementTags && string.IsNullOrEmpty(imp_cc.tagError))
					{
						//special case: use custom code with replacement tags
						result += imp_cc.PrintVariableReplacement(ref usedImplementations, inputSource, outputSource, arguments, program);
					}
					else if (imp_hsv != null)
					{
						//special case: apply hsv modifier to used implementations so far
						result = imp_hsv.PrintVariableHSV(result);
					}
					else
					{
						if (!usedImplementations.Contains(imp))
						{
							string variable = null;
							if (program == ProgramType.Vertex)
								variable = imp.PrintVariableVertex(inputSource, outputSource, arguments);
							else if (program == ProgramType.Fragment)
								variable = imp.PrintVariableFragment(inputSource, outputSource, arguments);

							if (variable == null)
							{
								continue;
							}

							if (i > 0 && imp.HasOperator())
								result += imp.PrintOperator();
							result += variable;
						}
					}
				}

				if (declareVariable)
				{
					if (IsUsedInLightingFunction && ShaderGenerator2.CurrentPassHasLightingFunction && !isLayerClone && layerCloneSuffix == null)
					{
						return string.Format("{0}.{1} = {3}( {2} );", outputSource, GetVariableName(), result, prefix);
					}
					else
					{
						return string.Format("{0} {1} = {3}( {2} );", VariableTypeToShaderCode(Type), GetVariableName(), result, prefix);
					}
				}
				else
				{
					return string.Format("( {0} )", result);
				}
			}

			public virtual string PrintVariableSampleDeferred(string inputSource, string outputSource, ProgramType program, string args, bool declareVariable)
			{
				// HACK if in lighting function, add .input to the surface output struct when deferred sampling variables
				if (program == ProgramType.Fragment && ShaderGenerator2.IsInLightingFunction)
				{
					inputSource += ".input";
				}

				string variableSample = PrintVariableSample(inputSource, outputSource, program, args, declareVariable);
				string genericImps = PrintGenericImplementations();
				if (!string.IsNullOrEmpty(genericImps))
				{
					variableSample = string.Format("( {0}{1} )", variableSample, genericImps);
				}
				return variableSample;
			}

			//Print the variable name, optionally with "input." prefix if used in lighting function
			public string PrintVariableName(string inputSource)
			{
				string variableName = null;

				if (IsUsedInLightingFunction && ShaderGenerator2.CurrentPassHasLightingFunction)
				{
					variableName = string.Format("{0}.{1}", inputSource, GetVariableName());
				}
				else
				{
					variableName = GetVariableName();
				}

				// Generic Implementations have to be calculated when the Shader Property is sampled
				string genericImps = PrintGenericImplementations();
				if (!string.IsNullOrEmpty(genericImps))
				{
					variableName = string.Format("( {0}{1} )", variableName, genericImps);
				}

				return variableName;
			}

			string PrintGenericImplementations()
			{
				string genericImps = "";
				for(int i = 0; i < implementations.Count; i++)
				{
					if (usedImplementationsForCustomCode.Contains(i))
					{
						continue;
					}

					var genImp = implementations[i] as Imp_GenericFromTemplate;
					if (genImp != null)
					{
						genericImps += genImp.Print();
					}
				}
				return genericImps;
			}

			//Returns an array of needed features for this Shader Property to work (redundant values will be trimmed afterwards)
			public string[] NeededFeatures()
			{
				var features = new List<string>();
				foreach (var imp in implementations)
				{
					foreach (var nf in imp.NeededFeatures())
					{
						features.AddRange(GetNeededFeatures(nf, Program));
					}
					features.AddRange(imp.NeededFeaturesExtra());
				}

				if (clonedShaderProperties.Count > 0)
				{
					foreach (ShaderProperty clonedShaderProperty in clonedShaderProperties.Values)
					{
						features.AddRange(clonedShaderProperty.NeededFeatures());
					}
				}

				return features.ToArray();
			}

			static string[] GetNeededFeatures(OptionFeatures feature, ProgramType program)
			{
				switch (feature)
				{
					case OptionFeatures.VertexColors:
					{
						if (program == ProgramType.Fragment)
						{
							return new[] { "USE_VERTEX_COLORS_FRAG", "USE_VERTEX_COLORS_VERT" };
						}
						else
						{
							return new[] { "USE_VERTEX_COLORS_VERT" };
						}
					}

					case OptionFeatures.UV_Anim_Sine:
					{
						if (program == ProgramType.Fragment)
						{
							return new[] { "UV_SINE_ANIMATION_VERTEX", "UV_SINE_ANIMATION_FRAGMENT" };
						}
						else
						{
							return new[] { "UV_SINE_ANIMATION_VERTEX" };
						}
					}

					case OptionFeatures.UV_Anim_Sine_World:
					{
						if (program == ProgramType.Fragment)
						{
							return new[] { "UV_SINE_ANIMATION_VERTEX_WORLD", "UV_SINE_ANIMATION_FRAGMENT_WORLD", "USE_WORLD_POSITION_UV_VERTEX" };
						}
						else
						{
							return new[] { "UV_SINE_ANIMATION_VERTEX_WORLD", "USE_WORLD_POSITION_UV_VERTEX" };
						}
					}

					case OptionFeatures.NoTile_Sampling: return new[] { "NOTILE_SAMPLING" };
					case OptionFeatures.NoTile_Sampling_Vertex: return new[] { "NOTILE_SAMPLING_VERTEX" };
					case OptionFeatures.Triplanar_Sampling: return new[] { "TRIPLANAR_SAMPLING" };
					case OptionFeatures.Triplanar_Sampling_Global: return new[] { "TRIPLANAR_SAMPLING_GLOBAL" };
					case OptionFeatures.Triplanar_Sampling_Local: return new[] { "TRIPLANAR_SAMPLING_LOCAL" };
					case OptionFeatures.Triplanar_Sampling_Vertex: return new[] { "TRIPLANAR_SAMPLING_VERTEX" };
					case OptionFeatures.HSV_Full: return new[] { "USE_HSV_FULL" };
					case OptionFeatures.HSV_Grayscale: return new[] { "USE_HSV_GRAYSCALE" };
					case OptionFeatures.HSV_Colorize: return new[] { "USE_HSV_COLORIZE" };
					case OptionFeatures.Screen_Space_UV_Vertex: return new[] { "USE_SCREEN_SPACE_UV_VERTEX" };
					case OptionFeatures.Screen_Space_UV_Fragment: return new[] { "USE_SCREEN_SPACE_UV_FRAGMENT" };
					case OptionFeatures.Screen_Space_UV_Object_Offset: return new[] { "SCREEN_SPACE_UV_OBJECT_OFFSET" };
					case OptionFeatures.UV_Anim_Random_Offset: return new[] { "HASH_22" };
					case OptionFeatures.World_Pos_UV_Fragment: return new[] { "USE_WORLD_POSITION_FRAGMENT" };
					case OptionFeatures.World_Pos_UV_Vertex: return new[] { "USE_WORLD_POSITION_UV_VERTEX" };
					case OptionFeatures.Local_Pos_Fragment: return new[] { "USE_OBJECT_POSITION_FRAGMENT" };
					case OptionFeatures.Local_Normal_Fragment: return new[] { "USE_OBJECT_NORMAL_FRAGMENT" };
					case OptionFeatures.World_Normal_Vertex: return new[] { "USE_WORLD_NORMAL_UV_VERTEX" };
					case OptionFeatures.World_Normal_Fragment: return new[] { "USE_WORLD_NORMAL_FRAGMENT" };
				}

				return new string[0];
			}

			public static string[] AllOptionFeatures()
			{
				return new string[]
				{
					"USE_VERTEX_COLORS_FRAG",
					"USE_VERTEX_COLORS_VERT",
					"NOTILE_SAMPLING",
					"NOTILE_SAMPLING_VERTEX",
					"USE_HSV_FULL",
					"USE_HSV_GRAYSCALE",
					"USE_HSV_COLORIZE",
					"USE_SCREEN_SPACE_UV",
					"USE_SCREEN_SPACE_UV_FRAGMENT",
					"SCREEN_SPACE_UV_OBJECT_OFFSET",
					"HASH_22"
				};
			}

			internal string GetVariableName()
			{
				if (VariableTypeIsFixedFunction(Type))
				{
					// There can only be one implementation for fixed function properties
					return implementations[0].PrintVariableFixedFunction();
				}

				if (layerCloneSuffix != null)
				{
					return string.Format("__{0}_{1}", ToLowerCamelCase(this.Name), layerCloneSuffix);
				}

				return string.Format("__{0}", ToLowerCamelCase(this.Name, this.isMaterialLayerProperty || this.isLayerClone));
			}

			internal static string ToLowerCamelCase(string input, bool keepUnderscores = false)
			{
				string output = "";
				bool upper = false;
				for (int i = 0; i < input.Length; i++)
				{
					if (char.IsLetterOrDigit(input[i]) || (keepUnderscores && input[i] == '_'))
					{
						output += upper ? char.ToUpperInvariant(input[i]) : char.ToLowerInvariant(input[i]);
						upper = false;
					}
					else
					{
						upper = true;
					}
				}
				return output;
			}

			public struct MenuItem
			{
				public GUIContent guiContent;
				public bool disabled;
				public bool on;
				public GenericMenu.MenuFunction menuFunction;
				public GenericMenu.MenuFunction2 menuFunction2;
				public object args;
				public int order;
				public bool isSeparator;
				public string separatorPath;
			}

			bool IsImplementationCompatible(Type implementationType)
			{
				var compatibility = implementationType.GetProperty("VariableCompatibility", BindingFlags.Public | BindingFlags.Static);
				return (compatibility != null && CheckVariableType((VariableType)compatibility.GetValue(null, null), Type));
			}

			GenericMenu CreateImplementationsMenu(int index, bool add)
			{
				//create menu for available implementations
				var itemsList = new List<MenuItem>();
				var types = typeof(ShaderProperty).GetNestedTypes();
				bool hasGenericImpls = false;
				foreach (var t in types)
				{
					if (t.IsSubclassOf(typeof(Implementation)))
					{
						if (t == typeof(Imp_Hook))
						{
							continue;
						}

						if (t == typeof(Imp_GenericFromTemplate))
						{
							if (this.Type == VariableType.fixed_function_enum || this.Type == VariableType.fixed_function_float)
							{
								continue;
							}

							int order = Array.IndexOf(Implementation.MenuOrders, t) * 1000;
							var selectedImp = add ? null : implementations[index] as Imp_GenericFromTemplate;

							// Get available generic implementations and build menu options
							for (int i = 0; i < Imp_GenericFromTemplate.AvailableGenericImplementations.Count; i++)
							{
								var imp = Imp_GenericFromTemplate.AvailableGenericImplementations[i];
								bool selected = selectedImp != null && selectedImp.sourceIdentifier == imp.identifier;

								// different pass (note: pass 0 sets bit 1, etc.)
								if ((this.passBitmask & (1<<imp.pass)) == 0)
								{
									continue;
								}

								// same "callback" as below, except for 'newImp' being cloned instead of dynamically created
								GenericMenu.MenuFunction callback = () =>
								{
									//remove existing to prevent false positive unique name mismatch
									Implementation temp = null;
									if (!add)
									{
										//don't do anything if the same type is selected
										if (selected)
											return;

										temp = implementations[index];
										implementations[index].WillBeRemoved();
										implementations[index] = null;
									}

									var newImp = imp.CreateImplementation(this);
									if (add)
									{
										implementations.Insert(index, newImp);
									}
									else
									{
										newImp.CopyCommonProperties(temp);
										temp = null;
										implementations[index] = newImp;
									}

									CheckHash();
									CheckErrors();
									CallOnImplementationsChanged();
								};

								bool disabled = false;

								// check compatibility
								disabled = !imp.compatibleShaderProperties.Contains(this);

								if (disabled)
								{
									/*
									string suffix = " (calculated elsewhere in code)";
									itemsList.Add(new MenuItem { disabled = true, order = order + i, guiContent = new GUIContent(imp.MenuLabel + suffix) });
									*/
								}
								else
								{
									if (!hasGenericImpls)
									{
										hasGenericImpls = true;
										itemsList.Add(new MenuItem() { order = order + i - 1, isSeparator = true, separatorPath = "Special/" });
									}

									itemsList.Add(new MenuItem { order = order + i, guiContent = new GUIContent(imp.MenuLabel), on = selected, menuFunction = callback });
								}
							}

							continue;
						}

						if (IsImplementationCompatible(t))
						{
							int order = Array.IndexOf(Implementation.MenuOrders, t) * 1000;
							string label = t.GetProperty("MenuLabel", BindingFlags.Public | BindingFlags.Static).GetValue(null, null) as string;
							bool selected = add ? false : implementations[index].GetType() == t;

							//Imp_CustomMaterialProperty: disable if there isn't any custom material property defined, or add list of defined custom material property
							if (t == typeof(Imp_CustomMaterialProperty))
							{
								if (ShaderGenerator2.CurrentConfig.CustomMaterialProperties == null || ShaderGenerator2.CurrentConfig.CustomMaterialProperties.Length == 0)
								{
									itemsList.Add(new MenuItem { order = order, guiContent = new GUIContent(label), disabled = true });
								}
								else if (this.cantReferenceOtherProperties)
								{
									itemsList.Add(new MenuItem { order = order, guiContent = new GUIContent(label), disabled = true });
								}
								else
								{
									var ctImp = add ? null : implementations[index] as Imp_CustomMaterialProperty;

									GenericMenu.MenuFunction2 ctCallback = (object data) =>
									{
										var ct = data as CustomMaterialProperty;

										//only replace custom material property instance if same type
										if (!add && implementations[index].GetType() == t)
										{
											(implementations[index] as Imp_CustomMaterialProperty).LinkedCustomMaterialProperty = ct;
											(implementations[index] as Imp_CustomMaterialProperty).InitChannelsSwizzle();
										}
										//else create a new custom material property implementation
										else
										{
											var newImp = Activator.CreateInstance(t, new object[] { this }) as Imp_CustomMaterialProperty;
											newImp.LinkedCustomMaterialProperty = ct;
											newImp.InitChannelsSwizzle();
											if (add)
											{
												implementations.Insert(index, newImp);
											}
											else
											{
												newImp.CopyCommonProperties(implementations[index]);
												implementations[index].WillBeRemoved();
												implementations[index] = newImp;
											}
											CallOnImplementationsChanged();
										}

										CheckHash();
										CheckErrors();
										CallOnImplementationsChanged();
									};

									foreach (var ct in ShaderGenerator2.CurrentConfig.CustomMaterialProperties)
									{
										//add each custom material property as an option
										selected = add ? false : ctImp != null && ctImp.LinkedCustomMaterialProperty == ct;
										itemsList.Add(new MenuItem { order = order, guiContent = new GUIContent(string.Format("{0}/{1} ({2})", label, ct.Label, ct.PropertyName)), on = selected, menuFunction2 = ctCallback, args = ct });
									}
								}
							}
							//Imp_ShaderPropertyReference: disable if there isn't any other shader property available, or add list of other shader properties
							else if (t == typeof(Imp_ShaderPropertyReference))
							{
								if (ShaderGenerator2.CurrentConfig.VisibleShaderProperties == null || ShaderGenerator2.CurrentConfig.VisibleShaderProperties.Length == 0)
									itemsList.Add(new MenuItem { order = order, guiContent = new GUIContent(label), disabled = true });
								else if (this.cantReferenceOtherProperties)
									itemsList.Add(new MenuItem { order = order, guiContent = new GUIContent(label), disabled = true });
								else
								{
									var spRefImp = add ? null : implementations[index] as Imp_ShaderPropertyReference;

									GenericMenu.MenuFunction2 spCallback = (object data) =>
									{
										var sp = data as ShaderProperty;

										//only replace shader property instance if same type
										if (!add && implementations[index].GetType() == t)
										{
											(implementations[index] as Imp_ShaderPropertyReference).LinkedShaderProperty = sp;
										}
										//else create a new shader property implementation
										else
										{
											var newImp = Activator.CreateInstance(t, new object[] { this }) as Imp_ShaderPropertyReference;
											newImp.LinkedShaderProperty = sp;
											if (add)
											{
												implementations.Insert(index, newImp);
											}
											else
											{
												newImp.CopyCommonProperties(implementations[index]);
												implementations[index].WillBeRemoved();
												implementations[index] = newImp;
											}
											CallOnImplementationsChanged();
										}

										CheckHash();
										CheckErrors();
										CallOnImplementationsChanged();
									};

									var list = new List<ShaderProperty>(ShaderGenerator2.CurrentConfig.VisibleShaderProperties);
									list.Sort((x, y) => string.Compare(x.Name, y.Name));
									for (int i = 0; i <list.Count; i++)
									{
										var sp = list[i];

										//avoid cyclic reference
										if (sp == this)
											continue;

										string referenceError = Imp_ShaderPropertyReference.IsReferencePossible(this, sp);

										if (referenceError != "")
										{
											//add each shader property as an option
											selected = add ? false : spRefImp != null && spRefImp.LinkedShaderProperty == sp;
											if (referenceError != null)
												itemsList.Add(new MenuItem { order = order + i, guiContent = new GUIContent(label + "/" + sp.DisplayName + " " + referenceError), disabled = true });
											else
												itemsList.Add(new MenuItem { order = order + i, guiContent = new GUIContent(label + "/" + sp.DisplayName), on = selected, menuFunction2 = spCallback, args = sp });
										}
									}
								}
							}
							//general case: just add the implementation type as new imp
							else
							{
								GenericMenu.MenuFunction callback = () =>
								{
									//remove existing to prevent false positive unique name mismatch
									Implementation temp = null;
									if (!add)
									{
										//don't do anything if the same type is selected
										if (implementations[index].GetType() == t)
											return;

										temp = implementations[index];
										implementations[index].WillBeRemoved();
										implementations[index] = null;
									}

									var newImp = Activator.CreateInstance(t, new object[] { this }) as Implementation;
									if (add)
									{
										implementations.Insert(index, newImp);
									}
									else
									{
										newImp.CopyCommonProperties(temp);
										temp = null;
										implementations[index] = newImp;
									}

									CheckHash();
									CheckErrors();
									CallOnImplementationsChanged();
								};

								bool disabled = false;
								string suffix = "";

								// can only add one Imp_HSV per Shader Property
								if (t == typeof(Imp_HSV))
								{
									if (implementations.Exists(imp => imp is Imp_HSV))
									{
										disabled = true;
										suffix = " (already added)";
									}
								}

								if (disabled)
								{
									itemsList.Add(new MenuItem { disabled = true, order = order, guiContent = new GUIContent(label + suffix) });
								}
								else
								{
									itemsList.Add(new MenuItem { order = order, guiContent = new GUIContent(label + suffix), on = selected, menuFunction = callback });
								}
							}
						}
					}
				}

				//sort items list and build menu
				var implementationsMenu = new GenericMenu();
				itemsList.Sort((item1, item2) => item1.order.CompareTo(item2.order));
				foreach (var item in itemsList)
				{
					if(item.isSeparator)
						implementationsMenu.AddSeparator(item.separatorPath);
					else if (item.disabled)
						implementationsMenu.AddDisabledItem(item.guiContent);
					else if (item.menuFunction2 != null)
						implementationsMenu.AddItem(item.guiContent, item.on, item.menuFunction2, item.args);
					else
						implementationsMenu.AddItem(item.guiContent, item.on, item.menuFunction);
				}
				return implementationsMenu;
			}

			static readonly GUIContent gc_copyImplementations = new GUIContent("Copy Implementations");
			static readonly GUIContent gc_PasteImplementations = new GUIContent("Paste Implementations");
			static GUIContent gc_cantPasteImplementations = new GUIContent();
			static readonly GUIContent gc_ExportImplementations = new GUIContent("Export Implementations...");
			static readonly GUIContent gc_ImportImplementations = new GUIContent("Import Implementations...");
			static readonly GUIContent gc_ResetImplementations = new GUIContent("Reset Default Implementation");
			static readonly GUIContent gc_ResetImplementationsML = new GUIContent("Reset Default Implementation (keep Material Layers)");
			static readonly GUIContent gc_debugCompareImplementations = new GUIContent("Debug: compare implementations with defaults");
			static List<Implementation> s_copiedImplementationsBuffer;
			static ShaderProperty.VariableType s_copiedImplementationsType;

			/// <summary>
			/// Prevent an Implementation field/property from being copied/pasted
			/// </summary>
			[AttributeUsage(AttributeTargets.Field)]
			public class ExcludeFromCopy : Attribute { }

			void ShowContextMenu()
			{
				GenericMenu menu = new GenericMenu();

				if (Type == VariableType.fixed_function_enum)
				{
					menu.AddDisabledItem(gc_copyImplementations);
					menu.AddDisabledItem(gc_PasteImplementations);
					menu.AddSeparator("");
					menu.AddDisabledItem(gc_ExportImplementations);
					menu.AddDisabledItem(gc_ImportImplementations);
				}
				else
				{
					menu.AddItem(gc_copyImplementations, false, OnCopyImplementations);

					// verify that the copied implementations can be pasted on the target
					string cantPasteMessage = "";
					if (s_copiedImplementationsBuffer != null)
					{
						if (s_copiedImplementationsType != this.Type)
						{
							cantPasteMessage = " (incompatible type)";
						}
						else
						{
							var newImplementations = FilterCopiedImplementations(s_copiedImplementationsBuffer);

							if (newImplementations.Count > 0)
							{
								cantPasteMessage = null;
								menu.AddItem(gc_PasteImplementations, false, OnPasteImplementations, newImplementations);
							}
							else
							{
								cantPasteMessage = " (incompatible type)";
							}
						}
					}

					if (cantPasteMessage != null)
					{
						gc_cantPasteImplementations.text = string.Format("{0}{1}", gc_PasteImplementations.text, cantPasteMessage);
						menu.AddDisabledItem(gc_cantPasteImplementations);
					}

					menu.AddSeparator("");
					menu.AddItem(gc_ExportImplementations, false, OnExportImplementations);
					menu.AddItem(gc_ImportImplementations, false, OnImportImplementations);
				}

				menu.AddSeparator("");
				menu.AddItem(gc_ResetImplementations, false, OnResetImplementation, true);
				menu.AddItem(gc_ResetImplementationsML, false, OnResetImplementation, null);

				if (ShaderGenerator2.DEBUG_MODE)
				{
					menu.AddItem(gc_debugCompareImplementations, false, () =>
					{
						var method = typeof(ShaderProperty.Implementation).GetMethod("CompareToDefaultImplementation", BindingFlags.Instance | BindingFlags.NonPublic);
						foreach (var imp in this.implementations)
						{
							var genericMethod = method.MakeGenericMethod(imp.GetType());
							genericMethod.Invoke(imp, null);
						}
					});
				}

				menu.ShowAsContext();
			}

			List<Implementation> FilterCopiedImplementations(List<Implementation> implementationsToCopy)
			{
				var newImplementations = new List<Implementation>();
				foreach (var imp in implementationsToCopy)
				{
					var type = imp.GetType();
					if (!IsImplementationCompatible(type))
					{
						continue;
					}

					// TODO same for Imp_MaterialProperty_Texture when using Shader Property UV ?
					if (type == typeof(Imp_ShaderPropertyReference))
					{
						if (((Imp_ShaderPropertyReference)imp).LinkedShaderProperty != null && Imp_ShaderPropertyReference.IsReferencePossible(this, ((Imp_ShaderPropertyReference)imp).LinkedShaderProperty) != null)
						{
							continue;
						}
					}

					var newImplementation = (Implementation)Activator.CreateInstance(type, new object[] { this });

					var fields = type.GetFields();
					foreach (var field in fields)
					{
						var serializedAttributes = field.GetCustomAttributes(typeof(Serialization.SerializeAsAttribute), true);
						if (serializedAttributes.Length == 0)
						{
							continue;
						}

						var excludeAttributes = field.GetCustomAttributes(typeof(ExcludeFromCopy), true);
						if (excludeAttributes.Length > 0)
						{
							continue;
						}

						var value = field.GetValue(imp);
						field.SetValue(newImplementation, value);
					}

					newImplementations.Add(newImplementation);
				}
				return newImplementations;
			}

			void OnCopyImplementations()
			{
				s_copiedImplementationsBuffer = new List<Implementation>();

				foreach (var imp in implementations)
				{
					if (imp.CanBeCopied())
					{
						s_copiedImplementationsBuffer.Add(imp);
					}
				}

				s_copiedImplementationsType = this.Type;
			}

			void OnPasteImplementations(object newImplementations)
			{
				// Clear implementations except hooks
				implementations = implementations.Where(imp =>imp is Imp_Hook).ToList();

				foreach (var imp in (List<Implementation>)newImplementations)
				{
					imp.OnPasted();
				}
				implementations.AddRange((List<Implementation>)newImplementations);
				CheckErrors();
				CheckHash();
				CallOnImplementationsChanged();
			}

			void OnExportImplementations()
			{
				var folder = ProjectOptions.data.LastImplementationExportImportPath;
				if (!System.IO.Directory.Exists(folder))
				{
					folder = Application.dataPath;
				}

				var path = EditorUtility.SaveFilePanel("Export Implementations", folder, this.Name, "tcp2imp");
				if (!string.IsNullOrEmpty(path))
				{
					ProjectOptions.data.LastImplementationExportImportPath = System.IO.Path.GetDirectoryName(path);
					string output = "";
					foreach (var imp in implementations)
					{
						output += string.Format("{0}\n", Serialization.Serialize(imp));
					}
					System.IO.File.WriteAllText(path, output);
				}
			}

			void OnImportImplementations()
			{
				var path = EditorUtility.OpenFilePanel("Import Implementations", ProjectOptions.data.LastImplementationExportImportPath, "tcp2imp");
				if (!string.IsNullOrEmpty(path))
				{
					ProjectOptions.data.LastImplementationExportImportPath = System.IO.Path.GetDirectoryName(path);

					string[] serializedImplementations = System.IO.File.ReadAllLines(path);
					if (serializedImplementations.Length > 0)
					{
						List<Implementation> importedImplementations = new List<Implementation>();
						implementations.Clear();
						foreach (var serImp in serializedImplementations)
						{
							try
							{
								var imp = (Implementation)Serialization.Deserialize(serImp, new object[] { this });
								importedImplementations.Add(imp);
							}
							catch (Exception error)
							{
								Debug.LogError(ShaderGenerator2.ErrorMsg(string.Format("Couldn't deserialize the following line from tcp2imp file:\n\"{0}\"\nError returned:\n{1}", serImp, error.ToString())));
							}
						}

						if (importedImplementations.Count > 0)
						{
							var newImplementations = FilterCopiedImplementations(importedImplementations);
							if (newImplementations.Count > 0)
							{
								OnPasteImplementations(newImplementations);
							}
							else
							{
								EditorUtility.DisplayDialog("Import Implementations", "No compatible implementations found for this Shader Property type (" + Type.ToString() + ")", "OK");
							}
						}
						else
						{
							EditorUtility.DisplayDialog("Import Implementations", "No valid implementations could be found in this file.", "OK");
						}
					}
					else
					{
						Debug.LogError(ShaderGenerator2.ErrorMsg(("Empty tcp2imp file!")));
					}
				}
			}

			public void ResolveShaderPropertyReferences()
			{
				foreach (var imp in implementations)
				{
					var impSpRef = imp as Imp_ShaderPropertyReference;
					if (impSpRef != null)
					{
						impSpRef.TryToFindLinkedShaderProperty();
					}

					var impMpTex = imp as Imp_MaterialProperty_Texture;
					if (impMpTex != null)
					{
						if (impMpTex.UvSource == Imp_MaterialProperty_Texture.UvSourceType.OtherShaderProperty)
						{
							impMpTex.TryToFindLinkedShaderProperty();
						}
						else if (impMpTex.UvSource == Imp_MaterialProperty_Texture.UvSourceType.CustomMaterialProperty)
						{
							impMpTex.TryToFindLinkedCustomMaterialProperty();
						}
					}

					var impCC = imp as Imp_CustomCode;
					if (impCC != null)
					{
						impCC.TryToFindPrependCodeBlock();
						impCC.CheckReplacementTags();
					}
				}

				CheckErrors();
			}

			// TODO move
			int matLayerTab = -1;
			float matLayerScroll;
			float matLayerScrollTarget;
			
			public void ShowGUILayout(float indentLeft = 0)
			{
				EditorGUI.BeginChangeCheck();

				var guiColor = GUI.color;
				GUI.color *= EditorGUIUtility.isProSkin || (manuallyModified && !isMaterialLayerProperty) || error ? Color.white : new Color(.75f, .75f, .75f, 1f);
				var style = EditorStyles.helpBox;
				if (error)
				{
					style = expanded ? TCP2_GUI.ErrorPropertyHelpBoxExp : TCP2_GUI.ErrorPropertyHelpBox;
				}
				else if (manuallyModified && !isMaterialLayerProperty)
				{
					style = expanded ? TCP2_GUI.EnabledPropertyHelpBoxExp : TCP2_GUI.EnabledPropertyHelpBox;
				}

				if (indentLeft > 0)
				{
					EditorGUILayout.BeginHorizontal();
					GUILayout.Space(indentLeft);
				}

				EditorGUILayout.BeginVertical(style);
				GUI.color = guiColor;

				var rect = EditorGUILayout.GetControlRect(GUILayout.Height(EditorGUIUtility.singleLineHeight));
				var guiContent = new GUIContent(DisplayName);
				var typeLabel = new GUIContent(": " + VariableTypeToName(Type));
				var programLabel = new GUIContent(Program.ToString());
				float labelWidth = TCP2_GUI.HeaderDropDown.CalcSize(guiContent).x;
				float typeWidth = SGUILayout.Styles.GrayMiniLabel.CalcSize(typeLabel).x;
				float programLabelWidth = SGUILayout.Styles.GrayMiniLabel.CalcSize(programLabel).x;
				float rightMenuButtonWidth = 16;

				// hover
				TCP2_GUI.DrawHoverRect(rect);

				// main foldout
				var foldoutRect = rect;
				foldoutRect.width -= rightMenuButtonWidth;
				EditorGUI.BeginChangeCheck();
				expanded = GUI.Toggle(foldoutRect, expanded, guiContent, TCP2_GUI.HeaderDropDown);
				if (EditorGUI.EndChangeCheck())
				{
					if (Event.current.alt || Event.current.control)
					{
						var state = expanded;
						foreach (var sp in ShaderGenerator2.CurrentConfig.VisibleShaderProperties)
						{
							sp.expanded = state;
						}
					}
				}

				// variable type (color, color_rgba, float, ...)
				rect = GUILayoutUtility.GetLastRect();
				var r = rect;
				r.x += labelWidth;
				r.width -= labelWidth;
				using (new EditorGUI.DisabledScope(true))
				{
					GUI.Label(r, typeLabel, EditorStyles.miniLabel);
				}

				// help icon if there's a help message
				bool hasHelpMessage = helpMessage != null;
				if (hasHelpMessage)
				{
					r = rect;
					r.x += labelWidth + typeWidth;
					r.width = 16;
					r.y += 1;
					GUI.Label(r, TCP2_GUI.TempContent(null, TCP2_GUI.SmallHelpIconTexture));

					bool mouseOver = r.Contains(Event.current.mousePosition);
					ShaderGenerator2.showDynamicTooltip |= mouseOver;
					if (mouseOver)
					{
						ShaderGenerator2.dynamicTooltip = helpMessage;
					}
				}

				// program type (vertex, fragment, lighting)
				r = rect;
				r.x += rect.width - programLabelWidth - rightMenuButtonWidth;
				r.width = programLabelWidth;
				if (!isMaterialLayerProperty)
				{
					using (new EditorGUI.DisabledScope(true))
					{
						GUI.Label(r, programLabel, EditorStyles.miniLabel);
					}
				}
				
				if (linkedMaterialLayers.Count > 0)
				{
					r.width = 20;
					r.x -= r.width;
#if !UNITY_2019_3_OR_NEWER
					r.y += 2;
#endif
					GUI.Label(r, TCP2_GUI.TempContent(null, TCP2_GUI.LayersIconTexture));

					bool mouseOver = r.Contains(Event.current.mousePosition);
					ShaderGenerator2.showDynamicTooltip |= mouseOver;
					if (mouseOver)
					{
						ShaderGenerator2.dynamicTooltip = "This property uses Material Layers:" + layersTooltip;
					}
				}

				// implementations copy/export/import menu
				r = rect;
				r.x += rect.width - rightMenuButtonWidth;
				r.width = rightMenuButtonWidth;
				bool showMenu = GUI.Button(r, GUIContent.none, TCP2_GUI.ContextMenuButton);
				showMenu |= Event.current.type == EventType.MouseDown && Event.current.button == 1 && Event.current.modifiers == EventModifiers.None && foldoutRect.Contains(Event.current.mousePosition);

				if (showMenu)
				{
					ShowContextMenu();
				}

				if (expanded)
				{
					// Material Layer tabs
					if (!this.isMaterialLayerProperty && ShaderGenerator2.CurrentConfig.materialLayers.Count > 0)
					{
						GUILayout.Space(6);
						EditorGUILayout.BeginHorizontal();
						{
#if UNITY_2019_3_OR_NEWER
							var labelStyle = EditorStyles.label;
#else
							var labelStyle = EditorStyles.miniLabel;
#endif
							GUILayout.Label("Material Layers:", labelStyle, GUILayout.ExpandWidth(false));
							matLayerTab = TabsHorizontalInfinite(matLayerTab + 1, ShaderGenerator2.CurrentConfig.materialLayersNames, ref matLayerScroll, ref matLayerScrollTarget) - 1;
						}
						EditorGUILayout.EndHorizontal();
					}

					bool disableUi = false;
					bool drawBaseShaderProperty = true;
					matLayerTab = Mathf.Clamp(matLayerTab, -1, ShaderGenerator2.CurrentConfig.materialLayers.Count - 1);
					if (matLayerTab >= 0)
					{
						// Draw Material Layer
						
						MaterialLayer materialLayer = ShaderGenerator2.CurrentConfig.materialLayers[matLayerTab];
						bool layerIsEnabled = linkedMaterialLayers.Contains(materialLayer.uid);
						EditorGUI.BeginChangeCheck();
						GUILayout.Space(EditorGUIUtility.standardVerticalSpacing);
						// --- horizontal
						EditorGUILayout.BeginHorizontal();
						bool toggle = GUILayout.Toggle(layerIsEnabled, TCP2_GUI.TempContent(string.Format(" Enable Layer '{0}'", materialLayer.name)));
						if (EditorGUI.EndChangeCheck())
						{
							if (toggle)
							{
								AddMaterialLayer(materialLayer.uid);
							}
							else
							{
								RemoveMaterialLayer(materialLayer.uid);
							}

							layerIsEnabled = linkedMaterialLayers.Contains(materialLayer.uid);
						}

						bool layerIsLocked = !unlockedMaterialLayers.Contains(materialLayer.uid);
						EditorGUI.BeginDisabledGroup(!layerIsEnabled);

						GUILayout.FlexibleSpace();

						if (layerIsEnabled)
						{
							// Re-synchronize dictionaries in case we are loading an older shader without blending support
							if (materialLayerBlendings.Count != linkedMaterialLayers.Count)
							{
								materialLayerBlendings.Clear();
								materialLayercustomBlendings.Clear();
								foreach (string uid in linkedMaterialLayers)
								{
									materialLayerBlendings.Add(uid, MaterialLayer.BlendType.LinearInterpolation);
									materialLayercustomBlendings.Add(uid, DefaultCustomBlending);
								}
							}

							GUILayout.Label(TCP2_GUI.TempContent("Layer Blending"), GUILayout.ExpandWidth(false));
							materialLayerBlendings[materialLayer.uid] = (MaterialLayer.BlendType)EditorGUILayout.EnumPopup(materialLayerBlendings[materialLayer.uid]);
							EditorGUILayout.EndHorizontal();
							// --- end horizontal
							if (materialLayerBlendings[materialLayer.uid] == MaterialLayer.BlendType.Custom)
							{
								EditorGUILayout.HelpBox("Define your custom blending formula here:\na: original property\nb: layer property\ns: layer source value", MessageType.Info);
								EditorGUILayout.BeginHorizontal();
								EditorGUI.BeginDisabledGroup(true);
								GUILayout.Label("a = ", GUILayout.ExpandWidth(false));
								EditorGUI.EndDisabledGroup();
								materialLayercustomBlendings[materialLayer.uid] = EditorGUILayout.TextField(materialLayercustomBlendings[materialLayer.uid]);
								EditorGUILayout.EndHorizontal();
								TCP2_GUI.SeparatorSimple();
							}
						}
						else
						{
							GUILayout.Label(TCP2_GUI.TempContent("Layer Blending"), GUILayout.ExpandWidth(false));
							EditorGUILayout.EnumPopup(MaterialLayer.BlendType.LinearInterpolation);
							EditorGUILayout.EndHorizontal();
							// --- end horizontal
						}

						EditorGUI.BeginChangeCheck();
						bool locked = GUILayout.Toggle(layerIsLocked, TCP2_GUI.TempContent(" Same as Base layer"));
						if (EditorGUI.EndChangeCheck())
						{
							if (!locked)
							{
								unlockedMaterialLayers.Add(materialLayer.uid);
								if (!clonedShaderProperties.ContainsKey(materialLayer.uid))
								{
									var cloneSp = this.CloneForLayer(materialLayer);
									clonedShaderProperties.Add(materialLayer.uid, cloneSp);
								}
							}
							else
							{
								unlockedMaterialLayers.Remove(materialLayer.uid);
							}
						}
						EditorGUI.EndDisabledGroup();

						disableUi = !layerIsEnabled || layerIsLocked;

						if (!locked)
						{
							EditorGUI.BeginDisabledGroup(disableUi);
							{
								clonedShaderProperties[materialLayer.uid].ShowImplementationsGUI();
								drawBaseShaderProperty = false;
							}
							EditorGUI.EndDisabledGroup();
						}
					}

					if (drawBaseShaderProperty)
					{
						EditorGUI.BeginDisabledGroup(disableUi);
						ShowImplementationsGUI();
						EditorGUI.EndDisabledGroup();
					}
				}

				EditorGUILayout.EndVertical();
				if (indentLeft > 0)
				{
					EditorGUILayout.EndHorizontal();
				}

				if (EditorGUI.EndChangeCheck())
				{
					CheckHash();
					CheckErrors();
				}
			}

			void ShowImplementationsGUI()
			{
				var guiColor = GUI.color;
				int removeAt = -1;
				int insertAt = -1;
				
				//lambda function so that we can reorder drawing when one is selected
				Action<int, float> DrawImplementation = (index, indent) =>
				{
					bool usedByCustomCode = usedImplementationsForCustomCode.Contains(index);

					if (index > 0)
					{
						GUILayout.Space(1);
						SGUILayout.DrawLine();
						GUILayout.Space(2);
					}
					else
						GUILayout.Space(6);

					GUILayout.BeginHorizontal();
					GUILayout.Space(indent);

					// button with implementation name, show imp menu on click
					if (index > 0 && implementations[index].HasOperator() && !usedByCustomCode)
					{
						var op = (int) implementations[index].@operator;
						if (GUILayout.Button(OperatorSymbols[op], EditorStyles.popup, GUILayout.Width(35)))
						{
							var menu = new GenericMenu();
							for (var j = 0; j < OperatorSymbols.Length; j++)
							{
								menu.AddItem(new GUIContent(OperatorSymbols[j]), false, implementations[index].SetOperator, j);
							}

							menu.ShowAsContext();
						}
					}
					else if (usedByCustomCode)
					{
						using (new EditorGUI.DisabledScope(true))
						{
							GUILayout.Button(new GUIContent("CC", "Used by Custom Code"), EditorStyles.miniButton, GUILayout.Width(35));
						}
					}

					bool locked = implementations[index].IsLocked;
					using (new EditorGUI.DisabledGroupScope(locked))
					{
						if (locked)
						{
							SGUILayout.DrawLockIcon(Color.gray);
						}

						string text = string.Format("{0}. {1}", index + 1, implementations[index].GUILabel());
						var label = new GUIContent(text, locked ? "This implementation is locked and can't be changed for this property, as it is required by the shader.\nYou can still add more implementations for this property though." : "");
						if (GUILayout.Button(label, EditorStyles.popup))
						{
							//create & show context menu
							var implementationsMenu = CreateImplementationsMenu(index, false);
							implementationsMenu.ShowAsContext();
						}
					}

					//Add/Remove MoveUp/MoveDown buttons
					if (!VariableTypeIsFixedFunction(Type))
					{
						const float w = UI.GUI_RIGHT_BUTTONS / 2;
						if (GUILayout.Button("+", EditorStyles.miniButtonLeft, GUILayout.Width(w)))
						{
							insertAt = index + 1;
						}

						using (new EditorGUI.DisabledGroupScope(implementations.Count <= 1 || locked))
						{
							if (GUILayout.Button("-", EditorStyles.miniButtonRight, GUILayout.Width(w)))
							{
								removeAt = index;
							}
						}
					}

					GUILayout.EndHorizontal();

					//Parameters depending on property type
					GUILayout.Space(1);
					implementations[index].NewLineGUI(usedByCustomCode);
				};

				//guiColor = GUI.color;
				GUI.color *= new Color(.92f, .92f, .92f, 1f);
				EditorGUILayout.BeginVertical(EditorStyles.helpBox);
				GUI.color = guiColor;
				{
					bool reorder = layoutList.DoLayoutList(DrawImplementation, implementations);
					if (reorder)
					{
						CallOnImplementationsChanged();
						CheckErrors();
					}
				}
				EditorGUILayout.EndVertical();
				
				//Add/Remove from list
				if (insertAt >= 0)
				{
					//create & show context menu
					var implementationsMenu = CreateImplementationsMenu(insertAt, true);
					implementationsMenu.ShowAsContext();
				}
				if (removeAt >= 0)
				{
					implementations[removeAt].WillBeRemoved();
					implementations.RemoveAt(removeAt);
					CallOnImplementationsChanged();
				}
			}

			static string GetMaterialLayerTabLabel(string label)
			{
#if UNITY_2019_3_OR_NEWER
				return string.Format(" {0} ", label);
#else
				return label;
#endif	
			}
			
			static readonly Color DisabledLayerColor = new Color(.7f, .7f, .7f);
			int TabsHorizontalInfinite(int selected, KeyValuePair<string, string>[] options, ref float scrollPosition, ref float targetScrollPosition)
			{
				var guiColor = GUI.color;
				EditorGUILayout.BeginHorizontal();
				{
#if UNITY_2019_3_OR_NEWER
					const float buttonHeight = 18;
#else
					const float buttonHeight = 15;
#endif
					var lineRect = EditorGUILayout.GetControlRect(GUILayout.Height(buttonHeight));
					
					// Calculate total space used by the tabs:
					float totalWidth = 0f;
					float[] widths = new float[options.Length];
					for (int i = 0; i < options.Length; i++)
					{
						var gc = TCP2_GUI.TempContent(GetMaterialLayerTabLabel(options[i].Key));
						var size = SGUILayout.Styles.MiniButtonMid.CalcSize(gc);
						widths[i] = size.x;
						totalWidth += widths[i];
					}
					float minValue = lineRect.width - totalWidth;

					// If remaining space is negative, then tabs can't fit in the current width:
					if (minValue < 0)
					{
						const float clipHeight = 18;

						Rect btnPrevRect = lineRect;
						btnPrevRect.width = 20;
						lineRect.xMin += btnPrevRect.width;
					
						Rect btnNextRect = lineRect;
						btnNextRect.width = 20;
						lineRect.xMax -= btnNextRect.width;
						btnNextRect.x = lineRect.xMax;

						// Take arrow button widths into account:
						minValue -= 40 - 2;

						lineRect.height = clipHeight;
						GUI.BeginClip(lineRect, new Vector2(scrollPosition, 0), Vector2.zero, false);
						{
							Rect r = new Rect(0, 0, 0, buttonHeight);
							for (int i = 0; i < options.Length; i++)
							{
								r.width = widths[i];

								bool layerIsEnabled = i == 0 || this.linkedMaterialLayers.Contains(options[i].Value);
								GUI.color = !layerIsEnabled ? DisabledLayerColor : guiColor;
								{
									var gc = TCP2_GUI.TempContent(GetMaterialLayerTabLabel(options[i].Key));
									if (GUI.Toggle(r, i == selected, gc, SGUILayout.Styles.MiniButtonMid))
									{
										selected = i;
									}
								}
								GUI.color = guiColor;

								r.x += r.width;
							}
						}
						GUI.EndClip();
						
						// Arrow buttons:
						using (new EditorGUI.DisabledScope(targetScrollPosition >= 0))
						{
							if (GUI.RepeatButton(btnPrevRect, TCP2_GUI.TempContent("<"), SGUILayout.Styles.MiniButtonLeft))
							{
								targetScrollPosition += 2;
							}
						}

						using (new EditorGUI.DisabledScope(targetScrollPosition <= minValue))
						{
							if (GUI.RepeatButton(btnNextRect, TCP2_GUI.TempContent(">"), SGUILayout.Styles.MiniButtonRight))
							{
								targetScrollPosition -= 2;
							}
						}

						targetScrollPosition = Mathf.Clamp(targetScrollPosition, minValue, 0);

						if (Event.current.type == EventType.Repaint)
						{
							if (Math.Abs(targetScrollPosition - scrollPosition) > 0.1f)
							{
								scrollPosition = Mathf.Lerp(scrollPosition, targetScrollPosition, Mathf.Max(0.05f, Time.deltaTime * 0.25f));
								ShaderGenerator.ShaderGenerator2.RepaintWindow();
							}
							else
							{
								scrollPosition = targetScrollPosition;
							}
						}
						
						scrollPosition = Mathf.Clamp(scrollPosition, minValue, 0);
					}
					else
					// Else the tabs can fit:
					{
						Rect rect = lineRect;
						rect.width = widths[0];
						
						// First button:
						if (GUI.Toggle(rect, selected == 0, TCP2_GUI.TempContent(GetMaterialLayerTabLabel(options[0].Key)), options.Length > 1 ? SGUILayout.Styles.MiniButtonLeft : SGUILayout.Styles.MiniButton))
						{
							selected = 0;
						}
						
						// Mid buttons
						for (int i = 1; i < options.Length - 1; i++)
						{
							rect.xMin += rect.width;
							rect.width = widths[i];

							bool layerIsEnabled = this.linkedMaterialLayers.Contains(options[i].Value);
							GUI.color = !layerIsEnabled ? DisabledLayerColor : guiColor;
							{
								if (GUI.Toggle(rect, selected == i, TCP2_GUI.TempContent(GetMaterialLayerTabLabel(options[i].Key)), SGUILayout.Styles.MiniButtonMid))
								{
									selected = i;
								}
							}
							GUI.color = guiColor;
						}
						
						// Last Button:
						rect.xMin += rect.width;
						rect.width = widths[widths.Length - 1];
						GUI.color = !linkedMaterialLayers.Contains(options[options.Length-1].Value) ? DisabledLayerColor : guiColor;
						{
							if (GUI.Toggle(rect, selected == options.Length - 1, TCP2_GUI.TempContent(GetMaterialLayerTabLabel(options[options.Length-1].Key)), options.Length > 1 ? SGUILayout.Styles.MiniButtonRight : SGUILayout.Styles.MiniButton))
							{
								selected = options.Length - 1;
							}
						}
						GUI.color = guiColor;
					}
				}
				EditorGUILayout.EndHorizontal();

				return selected;
			}

			static Dictionary<string, string> GetAssociatedData(string[] keyValuePairs, int startIndex = 0)
			{
				var associatedData = new Dictionary<string, string>();
				for (var j = startIndex; j < keyValuePairs.Length; j++)
				{
					var kvp = keyValuePairs[j].Trim();
					if (kvp.StartsWith("imp("))
					{
						continue;
					}

					var keyValue = kvp.Split('=');
					associatedData.Add(keyValue[0].Trim(), keyValue[1].Trim());
				}
				return associatedData;
			}

			static readonly Dictionary<string, ShaderProperty> CachedShaderPropertiesFromTemplate = new Dictionary<string, ShaderProperty>();

			public static void ClearCache()
			{
				CachedShaderPropertiesFromTemplate.Clear();
			}

			public static ShaderProperty CreateFromTemplateData(string line)
			{
				if (CachedShaderPropertiesFromTemplate.ContainsKey(line))
				{
					return CachedShaderPropertiesFromTemplate[line];
				}

				var data = line.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);
				var variableType = (VariableType)Enum.Parse(typeof(VariableType), data[0]);

				//create ShaderProperty
				var shaderProperty = new ShaderProperty(data[1], variableType);
				
				//create default implementation
				// - get associated data
				var subdata = Serialization.SplitExcludingBlocks(data[2], ',', true, true, "()");
				var programType = subdata[0].ToLowerInvariant();

				// - define program type (vertex, fragment)
				try
				{
					var type = programType;
					if (programType == "frag")
						type = "fragment";
					else if (programType == "vert")
						type = "vertex";
					else if (programType == "surface")
						type = "fragment";
					else if (programType == "lighting")
					{
						type = "fragment";
						shaderProperty.IsUsedInLightingFunction = true;
					}
					else if (programType == "fixed")
					{
						type = "FixedFunction";
					}
					shaderProperty.Program = (ProgramType)Enum.Parse(typeof(ProgramType), type, true);
				}
				catch
				{
					Debug.LogError(ShaderGenerator2.ErrorMsg("Unrecognized Shader Property program type: '" + programType + "'. It should be either '<b>vertex</b>' or '<b>fragment</b>'."));
				}

				// shaderProperty-specific data
				var associatedData = GetAssociatedData(subdata, 1);

				shaderProperty.deferredSampling = GetAssociatedDataBool(associatedData, "manually_sampled", false);
				shaderProperty.preventReference = GetAssociatedDataString(associatedData, "prevent_reference", null);
				shaderProperty.cantReferenceOtherProperties = GetAssociatedDataBool(associatedData, "cant_reference", false);
				shaderProperty.helpMessage = GetAssociatedDataString(associatedData, "help", null);
				shaderProperty.displayName = GetAssociatedDataString(associatedData, "label", null);

				// create the implementation(s)
				var list = new List<Implementation>();
				int i = 0;
				foreach(var sub in subdata)
				{
					var subTrim = sub.Trim();
					if (subTrim.StartsWith("imp("))
					{
						var imp = ParseImplementation(subTrim, shaderProperty);
						if(imp == null)
						{
							Debug.LogError(ShaderGenerator2.ErrorMsg("Couldn't parse implementation:\n" + subTrim));
						}
						else
						{
							imp.DefaultImplementationIndex = i;
							i++;
							list.Add(imp);
						}
					}
				}

				shaderProperty.SetDefaultImplementations(list.ToArray());

				// add cached so that they're not recreated at each SG2 change in the UI
				CachedShaderPropertiesFromTemplate.Add(line, shaderProperty);

				return shaderProperty;
			}

			// Parse a string-represented implementation, in the form:
			// imp(key = value, key2 = value2, key3 = value3, ...)
			static Implementation ParseImplementation(string strImplementation, ShaderProperty shaderProperty)
			{
				Implementation imp = null;

				int impLength = "imp(".Length;
				string impTrim = strImplementation.Substring(impLength, strImplementation.Length - impLength - 1);
				var impData = Serialization.SplitExcludingBlocks(impTrim, ',', true, "()");
				string impType = impData[0].Trim();
				var associatedData = GetAssociatedData(impData, 1);

				switch (impType)
				{
					case "texture":
					{
						imp = new Imp_MaterialProperty_Texture(shaderProperty)
						{
							DefaultValue = GetAssociatedDataString(associatedData, "default"),
							UvChannel = GetAssociatedDataInt(associatedData, "uv_channel", 0),
							UseTilingOffset = GetAssociatedDataBool(associatedData, "tiling_offset", false),
							GlobalTilingOffset = GetAssociatedDataBool(associatedData, "global", false),
							ScaleByTexelSize = GetAssociatedDataBool(associatedData, "scale_texel", false),
							UseScrolling = GetAssociatedDataBool(associatedData, "scrolling", false),
							GlobalScrolling = GetAssociatedDataBool(associatedData, "global_scrolling", false),
							RandomOffset = GetAssociatedDataBool(associatedData, "random_offset", false),
							GlobalRandomOffset = GetAssociatedDataBool(associatedData, "global_random_offset", false),
							MaterialDrawers = GetAssociatedDataString(associatedData, "drawer", ""),
							IsUvLocked = GetAssociatedDataBool(associatedData, "locked_uv", false),
							ChannelsCount = VariableTypeToChannelsCount(shaderProperty.Type),
							TilingOffsetVariable = GetAssociatedDataString(associatedData, "tiling_offset_var", ""),
							UVTriplanarScale = GetAssociatedDataFloat(associatedData, "triplanar_scale", 1.0f)
#if UNITY_2019_4_OR_NEWER
							, SeparateSamplerName = GetAssociatedDataString(associatedData, "sampler", null)
#endif
						};

						var channels = GetAssociatedDataString(associatedData, "channels", null);
						if (channels != null)
						{
							((Imp_MaterialProperty_Texture)imp).Channels = channels.ToUpperInvariant();
							((Imp_MaterialProperty_Texture)imp).ChannelsCount = channels.Length;
						}

						var uv_screenspace = GetAssociatedDataString(associatedData, "uv_screenspace", "");
						if (!string.IsNullOrEmpty(uv_screenspace))
						{
							((Imp_MaterialProperty_Texture)imp).SetScreenSpaceUV();
						}


						var uv_world_pos = GetAssociatedDataString(associatedData, "uv_worldpos", "");
						if (!string.IsNullOrEmpty(uv_world_pos))
						{
							((Imp_MaterialProperty_Texture)imp).SetWorldPositionUV();
						}

						var uv_triplanar = GetAssociatedDataString(associatedData, "uv_triplanar", "");
						if (!string.IsNullOrEmpty(uv_triplanar))
						{
							((Imp_MaterialProperty_Texture)imp).SetTriplanarUV();
						}

						var uv_shaderproperty = GetAssociatedDataString(associatedData, "uv_shaderproperty", "");
						if (!string.IsNullOrEmpty(uv_shaderproperty))
						{
							((Imp_MaterialProperty_Texture)imp).SetShaderPropertyUV();
							((Imp_MaterialProperty_Texture)imp).LinkedShaderPropertyName = uv_shaderproperty;

							var swizzle = GetAssociatedDataString(associatedData, "swizzle", null);
							if (!string.IsNullOrEmpty(swizzle))
							{
								((Imp_MaterialProperty_Texture)imp).UVChannels = swizzle;
							}
						}

						break;
					}

					case "float":
						imp = new Imp_MaterialProperty_Float(shaderProperty)
						{
							DefaultValue = GetAssociatedDataFloat(associatedData, "default"),
							MaterialDrawers = GetAssociatedDataString(associatedData, "drawer", "")
						};
						break;

					case "range":
						imp = new Imp_MaterialProperty_Range(shaderProperty)
						{
							DefaultValue = GetAssociatedDataFloat(associatedData, "default"),
							Min = GetAssociatedDataFloat(associatedData, "min"),
							Max = GetAssociatedDataFloat(associatedData, "max"),
							MaterialDrawers = GetAssociatedDataString(associatedData, "drawer", "")
						};
						break;

					case "vector":
					{
						var values = GetAssociatedDataString(associatedData, "default", "(0, 0, 0, 0)").TrimStart('(').TrimEnd(')');
						var defaultValueSplit = values.Split(new[] { "," }, StringSplitOptions.RemoveEmptyEntries);
						var defaultValue = Vector4.zero;
						defaultValue.x = defaultValueSplit.Length >= 1 ? float.Parse(defaultValueSplit[0], CultureInfo.InvariantCulture) : 0f;
						defaultValue.y = defaultValueSplit.Length >= 2 ? float.Parse(defaultValueSplit[1], CultureInfo.InvariantCulture) : 0f;
						defaultValue.z = defaultValueSplit.Length >= 3 ? float.Parse(defaultValueSplit[2], CultureInfo.InvariantCulture) : 0f;
						defaultValue.w = defaultValueSplit.Length >= 4 ? float.Parse(defaultValueSplit[3], CultureInfo.InvariantCulture) : 0f;
						imp = new Imp_MaterialProperty_Vector(shaderProperty)
						{
							DefaultValue = defaultValue,
							MaterialDrawers = GetAssociatedDataString(associatedData, "drawer", "")
						};
					}
					break;

					case "color":
					{
						var values = GetAssociatedDataString(associatedData, "default", "(0, 0, 0, 0)").TrimStart('(').TrimEnd(')');
						var defaultValueSplit = values.Split(new[] { "," }, StringSplitOptions.RemoveEmptyEntries);
						var defaultValue = Color.white;
						defaultValue.r = defaultValueSplit.Length >= 1 ? float.Parse(defaultValueSplit[0], CultureInfo.InvariantCulture) : 0f;
						defaultValue.g = defaultValueSplit.Length >= 2 ? float.Parse(defaultValueSplit[1], CultureInfo.InvariantCulture) : 0f;
						defaultValue.b = defaultValueSplit.Length >= 3 ? float.Parse(defaultValueSplit[2], CultureInfo.InvariantCulture) : 0f;
						defaultValue.a = defaultValueSplit.Length >= 4 ? float.Parse(defaultValueSplit[3], CultureInfo.InvariantCulture) : 1f;
						imp = new Imp_MaterialProperty_Color(shaderProperty)
						{
							DefaultValue = defaultValue,
							Hdr = GetAssociatedDataBool(associatedData, "hdr", false),
							MaterialDrawers = GetAssociatedDataString(associatedData, "drawer", "")
						};
					}
					break;

					case "vertex_color":
					{
						imp = new Imp_VertexColor(shaderProperty);
						var channels = GetAssociatedDataString(associatedData, "swizzle", null);
						if (!string.IsNullOrEmpty(channels))
							(imp as Imp_VertexColor).Channels = channels;
					}
					break;

					case "vertex_normal":
					{
						imp = new Imp_LocalNormal(shaderProperty);
						var channels = GetAssociatedDataString(associatedData, "swizzle", null);
						if (!string.IsNullOrEmpty(channels))
							(imp as Imp_LocalNormal).Channels = channels;
					}
					break;

					case "world_position":
					{
						imp = new Imp_WorldPosition(shaderProperty);
						var channels = GetAssociatedDataString(associatedData, "swizzle", null);
						if (!string.IsNullOrEmpty(channels))
							(imp as Imp_WorldPosition).Channels = channels;
					}
					break;

					case "mesh_world_position":
					{
						imp = new Imp_ObjectWorldPosition(shaderProperty);
						var channels = GetAssociatedDataString(associatedData, "swizzle", null);
						if (!string.IsNullOrEmpty(channels))
							(imp as Imp_ObjectWorldPosition).Channels = channels;
					}
					break;

					case "vertex_texcoord":
					{
						imp = new Imp_VertexTexcoord(shaderProperty);
						var channels = GetAssociatedDataString(associatedData, "swizzle", null);
						if (!string.IsNullOrEmpty(channels))
							(imp as Imp_VertexTexcoord).Channels = channels;
						var texcoordChannel = GetAssociatedDataInt(associatedData, "texcoord", -1);
						if (texcoordChannel >= 0)
							(imp as Imp_VertexTexcoord).TexcoordChannel = texcoordChannel;
					}
					break;

					case "custom_code":
					{
						imp = new Imp_CustomCode(shaderProperty)
						{
							code = GetAssociatedDataString(associatedData, "code")
						};
					}
					break;

					case "shader_property_reference":
					case "shader_property_ref":
					{
						var linkedPropertyName = GetAssociatedDataString(associatedData, "reference", null);
						var channels = GetAssociatedDataString(associatedData, "swizzle", null);
						imp = new Imp_ShaderPropertyReference(shaderProperty)
						{
							//only reference name here, the actual one will be retrieved later because it might not exist yet
							LinkedShaderPropertyName = linkedPropertyName,
							Channels = channels
						};
					}
					break;

					case "constant":
					{
						switch (shaderProperty.Type)
						{
							case VariableType.@float:
							case VariableType.fixed_function_float:
							case VariableType.fixed_function_enum:
								imp = new Imp_ConstantValue(shaderProperty)
								{
									FloatValue = GetAssociatedDataFloat(associatedData, "default", 0)
								};
								break;

							case VariableType.float2:
							case VariableType.float3:
							case VariableType.float4:
							{
								var values = GetAssociatedDataString(associatedData, "default", "(0, 0, 0, 0)").TrimStart('(').TrimEnd(')');
								var defaultValueSplit = values.Split(new[] { "," }, StringSplitOptions.RemoveEmptyEntries);
								var defaultValue = Vector4.zero;
								defaultValue.x = defaultValueSplit.Length >= 1 ? float.Parse(defaultValueSplit[0], CultureInfo.InvariantCulture) : 0f;
								defaultValue.y = defaultValueSplit.Length >= 2 ? float.Parse(defaultValueSplit[1], CultureInfo.InvariantCulture) : 0f;
								defaultValue.z = defaultValueSplit.Length >= 3 ? float.Parse(defaultValueSplit[2], CultureInfo.InvariantCulture) : 0f;
								defaultValue.w = defaultValueSplit.Length >= 4 ? float.Parse(defaultValueSplit[3], CultureInfo.InvariantCulture) : 0f;
								imp = new Imp_ConstantValue(shaderProperty)
								{
									Float2Value = defaultValue,
									Float3Value = defaultValue,
									Float4Value = defaultValue
								};
							}
							break;

							case VariableType.color:
							case VariableType.color_rgba:
							{
								var values = GetAssociatedDataString(associatedData, "default", "(0, 0, 0, 0)").TrimStart('(').TrimEnd(')');
								var defaultValueSplit = values.Split(new[] { "," }, StringSplitOptions.RemoveEmptyEntries);
								var defaultValue = Color.white;
								defaultValue.r = defaultValueSplit.Length >= 1 ? float.Parse(defaultValueSplit[0], CultureInfo.InvariantCulture) : 0f;
								defaultValue.g = defaultValueSplit.Length >= 2 ? float.Parse(defaultValueSplit[1], CultureInfo.InvariantCulture) : 0f;
								defaultValue.b = defaultValueSplit.Length >= 3 ? float.Parse(defaultValueSplit[2], CultureInfo.InvariantCulture) : 0f;
								defaultValue.a = defaultValueSplit.Length >= 4 ? float.Parse(defaultValueSplit[3], CultureInfo.InvariantCulture) : 1f;
								imp = new Imp_ConstantValue(shaderProperty)
								{
									ColorValue = defaultValue
								};
							}

							break;
						}
					}
					break;

					case "constant_float":
					{
						imp = new Imp_ConstantFloat(shaderProperty)
						{
							FloatValue = GetAssociatedDataFloat(associatedData, "default", 0)
						};
					}
					break;

					case "enum":
					{
						if (shaderProperty.Type != VariableType.fixed_function_enum)
						{
							Debug.LogError(ShaderGenerator2.ErrorMsg("Enum Implementation can only be used with Fixed Function Enum types."));
							break;
						}

						imp = new Imp_Enum(shaderProperty)
						{
							EnumType = GetAssociatedDataString(associatedData, "enum_type", null)
						};
						((Imp_Enum)imp).SetEnumType();

						int defaultValueInt = GetAssociatedDataInt(associatedData, "default", -1);
						if (defaultValueInt >= 0)
						{
							((Imp_Enum)imp).EnumValue = defaultValueInt;
						}
						else
						{
							string defaultValue = GetAssociatedDataString(associatedData, "default", null);
							if (!string.IsNullOrEmpty(defaultValue))
							{
								((Imp_Enum)imp).Parse(defaultValue.Trim('"'));
							}
						}

						break;
					}

					case "hook":
					{
						imp = new Imp_Hook(shaderProperty);
						shaderProperty.isHook = true;
						shaderProperty.deferredSampling = true;
						// shaderProperty.cantReferenceOtherProperties = true;
						shaderProperty.preventReference = "(hook)";
						shaderProperty.toggleFeatures = GetAssociatedDataString(associatedData, "toggles", null);
						break;
					}

					default:
						Debug.LogError(ShaderGenerator2.ErrorMsg("Unrecognized default property type: '" + impType + "'"));
						break;
				}

				if (imp != null)
				{
					// - common properties to all types
					imp.IsLocked |= GetAssociatedDataBool(associatedData, "locked", false);
					imp.Label = GetAssociatedDataString(associatedData, "label", shaderProperty.Name);

					// - specific to some implementations
					var imp_mp_texture = imp as Imp_MaterialProperty_Texture;
					if (imp_mp_texture != null)
					{
						if (imp_mp_texture.IsUvLocked)
						{
							// UVs are calculated in the shader, meaning that the property should be sampled when it is used rather than at the beginning of the vert or frag function
							shaderProperty.deferredSampling = true;
							shaderProperty.preventReference = "(sampled elsewhere in code)";
						}
						
						if (!string.IsNullOrEmpty(imp_mp_texture.TilingOffsetVariable))
						{
							imp_mp_texture.TilingOffsetVariableLabel = imp_mp_texture.TilingOffsetVariable;
						}

#if UNITY_2019_4_OR_NEWER
						imp_mp_texture.SamplerGroup = GetAssociatedDataInt(associatedData, "sampler_group", 0);
#endif
					}

					var imp_mp = imp as Imp_MaterialProperty;
					if (imp_mp != null)
					{
						// get specific variable name for material properties
						string propertyName = GetAssociatedDataString(associatedData, "variable", null);
						if (propertyName != null)
						{
							(imp as Imp_MaterialProperty).PropertyName = propertyName;
						}
						
						(imp as Imp_MaterialProperty).PropertyNameLocked = GetAssociatedDataBool(associatedData, "variable_locked", false);
					}
				}

				return imp;
			}

			//Get associated data with error/empty checks and default values
			static string GetAssociatedDataString(Dictionary<string, string> ad, string key, string defaultValue = "DefaultValue")
			{
				var str = defaultValue;
				if(ad.ContainsKey(key))
				{
					str = ad[key];

					//remove quotes
					if (str.StartsWith("\""))
					{
						str = str.Substring(1, str.Length - 2);
					}
				}
				return str;
			}
			static int GetAssociatedDataInt(Dictionary<string, string> ad, string key, int defaultValue = 0)
			{
				var ret = defaultValue;
				if (ad.ContainsKey(key))
				{
					if (!int.TryParse(ad[key], out ret))
					{
						return defaultValue;
					}
				}
				return ret;
			}
			static float GetAssociatedDataFloat(Dictionary<string, string> ad, string key, float defaultValue = 0.0f)
			{
				var ret = defaultValue;
				if (ad.ContainsKey(key))
				{
					if (!float.TryParse(ad[key], NumberStyles.Float, CultureInfo.InvariantCulture, out ret))
					{
						return defaultValue;
					}
				}
				return ret;
			}
			static bool GetAssociatedDataBool(Dictionary<string, string> ad, string key, bool defaultValue = false)
			{
				var ret = defaultValue;
				if (ad.ContainsKey(key))
				{
					if (!bool.TryParse(ad[key], out ret))
					{
						return defaultValue;
					}
				}
				return ret;
			}

			//Get arguments values
			static string TryGetArgument(string key, string arguments)
			{
				if (string.IsNullOrEmpty(arguments))
				{
					return null;
				}

				var args = arguments.Split(',');
				var heading = key + ":";
				foreach (var arg in args)
				{
					if (arg.StartsWith(heading))
					{
						return arg.Substring(arg.IndexOf(':')+1);
					}
				}

				return null;
			}

			static string AddArgument(string key, string value, string arguments)
			{
				return string.Format("{0}{1}:{2}",
					string.IsNullOrEmpty(arguments) ? "" : arguments + ",",
					key,
					value);
			}
		}
	}
}

// -----------------------------------------------------------------------------
// Merged from ShaderProperty.CustomMaterialProperty.cs
// -----------------------------------------------------------------------------

// Represents a user-created custom material property, that will be generated and injected in the code.
// It will be added as a Material Property in the shader, and can be used by any Shader Property.
//
// Main idea is to reuse channels from a texture between multiple features:
// - RGB = Albedo
// - A = Smoothness
// or
// - R = Smoothness, G = Rim strength, B = Subsurface Mask, A = Outline Width

namespace ToonyColorsPro
{
	namespace ShaderGenerator
	{
		public partial class ShaderProperty
		{
			[Serialization.SerializeAs("ct")]
			public class CustomMaterialProperty : IMaterialPropertyName
			{
				[Serialization.SerializeAs("cimp")]
				public Imp_MaterialProperty implementation;

				//================================================================================================================================
				// Deserialization

				[Serialization.CustomDeserializeCallback]
				static CustomMaterialProperty Deserialize(string data, object[] args)
				{
					ShaderProperty shaderProperty = ShaderGenerator2.CurrentConfig.customMaterialPropertyShaderProperty;

					// find the class name of the implementation, as it is needed to create an instance of CustomMaterialProperty
					string serializedClassName = data.Substring(data.IndexOf("cimp:") + "cimp:".Length);
					serializedClassName = serializedClassName.Substring(0, serializedClassName.IndexOf('('));
					Type implementationType = null;
					var allTypes = typeof(Serialization).Assembly.GetTypes();
					foreach (var t in allTypes)
					{
						var classAttributes = t.GetCustomAttributes(typeof(Serialization.SerializeAsAttribute), false);
						if (classAttributes != null && classAttributes.Length == 1)
						{
							var name = (classAttributes[0] as Serialization.SerializeAsAttribute).serializedName;
							if (name == serializedClassName)
							{
								//match!
								implementationType = t;
							}
						}
					}
					var customMaterialProperty = new CustomMaterialProperty(shaderProperty, implementationType);

					Func<object, string, object> onDeserializeImplementation = (impObj, impData) =>
					{
						// Make sure to deserialize as a new object, so that final Implementation subtype is kept instead of creating base Implementation class
						// Imp should only be an Imp_MaterialProperty
						var imp = Serialization.Deserialize(impData, new object[] { shaderProperty });
						return imp;
					};
					var implementationHandling = new Dictionary<Type, Func<object, string, object>> { { typeof(Imp_MaterialProperty), onDeserializeImplementation } };

					Serialization.DeserializeTo(customMaterialProperty, data, typeof(CustomMaterialProperty), args, implementationHandling);

					return customMaterialProperty;
				}

				//================================================================================================================================

				//Notification when a Custom Material Property is deleted
				public delegate void CustomTextureCallback(CustomMaterialProperty customTexture);
				public static event CustomTextureCallback OnCustomMaterialPropertyRemoved;

				//================================================================================================================================

				[Serialization.SerializeAs("exp")] bool expanded;
				[Serialization.SerializeAs("uv_exp")] bool uvExpanded;
				[Serialization.SerializeAs("imp_lbl")] public string implementationTypeLabel;

				public string Channels
				{
					get
					{
						switch(implementationTypeLabel)
						{
							case "Range":
							case "Float": return "XXXX";
							case "Vector": return "XYZW";
							case "Color":
							case "Texture": return "RGBA";
							default: return "RGBA";
						}
					}
				}

				public string GetChannelsForVariableType(VariableType variableType)
				{
					switch (variableType)
					{
						case VariableType.@float: return Channels.Substring(0, 1);
						case VariableType.float2: return Channels.Substring(0, 2);
						case VariableType.color:
						case VariableType.float3: return Channels.Substring(0, 3);
						case VariableType.color_rgba:
						case VariableType.float4: return Channels.Substring(0, 4);
					}
					return Channels;
				}

				//system to ensure each property name is unique
				public string GetPropertyName() { return implementation.PropertyName; }
				public string PropertyName
				{
					get { return implementation.PropertyName; }
					set { implementation.PropertyName = value; }
				}
				public string Label { get { return implementation.Label; } }
				public bool HasErrors { get { return implementation.HasErrors; } }
				public bool IsGpuInstanced { get { return implementation.IsGpuInstanced; } }
				public bool IsDotsInstanced { get { return implementation.IsDotsInstanced; } }

				internal OptionFeatures[] NeededFeatures() { return implementation.NeededFeatures(); }

				public CustomMaterialProperty(ShaderProperty sp, Type implementationType)
				{
					implementation = (Imp_MaterialProperty)Activator.CreateInstance(implementationType, new object[] { sp });
					var menuLabel = implementationType.GetProperty("MenuLabel", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
					string label = (string)menuLabel.GetValue(null, null);
					label = label.Replace("Material Property/", "");
					implementation.PropertyName = string.Format("_{0}", SGUILayout.Utils.RemoveWhitespaces("My " + label));
					implementation.Label = SGUILayout.Utils.VariableNameToReadable(implementation.PropertyName);
					implementation.IsCustomMaterialProperty = true;

					implementationTypeLabel = label;
				}

				public CustomMaterialProperty Clone()
				{
					return (CustomMaterialProperty)MemberwiseClone();
				}

				public void WillBeRemoved()
				{
					implementation.WillBeRemoved();

					if (OnCustomMaterialPropertyRemoved != null)
					{
						OnCustomMaterialPropertyRemoved(this);
					}
				}

				public override string ToString()
				{
					return "[CustomTexture " + PropertyName + ": " + implementation.ToString() + "]";
				}

				//Shader code output that goes in the ShaderLab Properties { } block
				public string PrintProperty(string indent)
				{
					return implementation.PrintProperty(indent);
				}

				//Shader code output that declares the variables, if any
				public string PrintVariablesDeclare(bool gpuInstanced, string indent)
				{
					if (implementation.IsGpuInstanced && !gpuInstanced
						|| !implementation.IsGpuInstanced && gpuInstanced)
					{
						return null;
					}

					return implementation.PrintVariableDeclare(indent);
				}

				public string PrintVariablesDeclareOutsideCBuffer(string indent)
				{
					return implementation.PrintVariableDeclareOutsideCBuffer(indent);
				}

				public string PrintVariableFragment()
				{
					// Only texture properties need sampling, others can use their variable name directly
					if (implementation is Imp_MaterialProperty_Texture)
					{
						return string.Format("value_{0}", PropertyName);
					}

					return PropertyName;
				}

				public string PrintVariableSurfaceOutput(VariableType variableType)
				{
					return string.Format("half{0} {1};", ShaderProperty.VariableTypeToChannelsCount(variableType), this.PrintVariableFragment());
				}

				public string PrintVariableVertex()
				{
					// Only texture properties need sampling, others can use their variable name directly
					if (implementation is Imp_MaterialProperty_Texture)
					{
						return string.Format("value_{0}", PropertyName);
					}

					return PropertyName;
				}

				public string SampleVariableFragment(string inputSource, string outputSource)
				{
					if (implementation is Imp_MaterialProperty_Texture)
					{
						// TODO variable precision option
						return string.Format("half{0} {1} = {2};\n", "4", this.PrintVariableFragment(), implementation.PrintVariableFragment(inputSource, outputSource, null));
					}

					return null;
				}

				public string SampleVariableVertex(string inputSource, string outputSource)
				{
					if (implementation is Imp_MaterialProperty_Texture)
					{
						return string.Format("half{0} {1} = {2};\n", "4", this.PrintVariableVertex(), implementation.PrintVariableVertex(inputSource, outputSource, null));
					}

					return null;
				}

				//================================================================================================================================

				public delegate void ButtonClick(int index);

				public void ShowGUILayout(int index, ButtonClick onAdd, ButtonClick onRemove)
				{
					var guiColor = GUI.color;
					GUI.color *= EditorGUIUtility.isProSkin || HasErrors ? Color.white : new Color(.75f, .75f, .75f, 1f);
					var style = EditorStyles.helpBox;
					if (HasErrors)
						style = expanded ? TCP2_GUI.ErrorPropertyHelpBoxExp : TCP2_GUI.ErrorPropertyHelpBox;
					EditorGUILayout.BeginVertical(style);
					GUI.color = guiColor;

					using (new SGUILayout.IndentedLine(16))
					{
						const float buttonWidth = 20;

						var rect = EditorGUILayout.GetControlRect(GUILayout.Height(EditorGUIUtility.singleLineHeight));
						var guiContent = new GUIContent(string.Format("{0} ({1})", Label, implementationTypeLabel));
						rect.width -= buttonWidth*2;

						// hover
						TCP2_GUI.DrawHoverRect(rect);

						EditorGUI.BeginChangeCheck();
						expanded = GUI.Toggle(rect, expanded, guiContent, TCP2_GUI.HeaderDropDown);
						if (EditorGUI.EndChangeCheck())
						{
							if (Event.current.alt || Event.current.control)
							{
								var state = expanded;
								foreach (var cmp in ShaderGenerator2.CurrentConfig.CustomMaterialProperties)
								{
									cmp.expanded = state;
								}
							}
						}

						var labelWidth = TCP2_GUI.HeaderDropDown.CalcSize(guiContent).x;
						var labelRect = GUILayoutUtility.GetLastRect();
						labelRect.x += labelWidth;
						labelRect.width -= labelWidth;
						using (new EditorGUI.DisabledScope(true))
						{
							GUI.Label(labelRect, ": " + PropertyName, EditorStyles.miniLabel);
						}
						
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

					if (expanded)
					{
						GUILayout.Space(4);

						implementation.NewLineGUI(false);
					}

					EditorGUILayout.EndVertical();
				}
			}
		}
	}
}

// -----------------------------------------------------------------------------
// Merged from ShaderProperty.Implementations.cs
// -----------------------------------------------------------------------------

// Implementations that can be used for Shader Properties

namespace ToonyColorsPro
{
	namespace ShaderGenerator
	{
		public partial class ShaderProperty
		{
			//Represents a Shader Property Implementation, e.g. a constant value, material property, vertex color channel...
			public class Implementation
			{
				//Defines the order in which menu item will appear in the menu
				public static Type[] MenuOrders = new Type[]
				{
					typeof(Imp_ConstantValue),
					typeof(Imp_ConstantFloat),
					typeof(Imp_MaterialProperty_Float),
					typeof(Imp_MaterialProperty_Range),
					typeof(Imp_MaterialProperty_Vector),
					typeof(Imp_MaterialProperty_Color),
					typeof(Imp_MaterialProperty_Texture),
					typeof(Imp_VertexColor),
					typeof(Imp_VertexTexcoord),
					typeof(Imp_LocalPosition),
					typeof(Imp_WorldPosition),
					typeof(Imp_LocalNormal),
					typeof(Imp_WorldNormal),
					typeof(Imp_ShaderPropertyReference),
					typeof(Imp_CustomMaterialProperty),
					typeof(Imp_HSV),
					typeof(Imp_CustomCode),
					typeof(Imp_GenericFromTemplate),
				};

				[Serialization.SerializeAs("guid")] public string guid;
				[Serialization.SerializeAs("op")] public Operator @operator = Operator.Multiply;      //How this implementation is calculated compared to the previous one
				[Serialization.SerializeAs("lbl"), ExcludeFromCopy] public string Label = "Property Label";
				[Serialization.SerializeAs("gpu_inst")] public bool IsGpuInstanced = false;
				[Serialization.SerializeAs("dots_inst")] public bool IsDotsInstanced = false;
				[Serialization.SerializeAs("locked"), ExcludeFromCopy] public bool IsLocked = false;
				[Serialization.SerializeAs("impl_index"), ExcludeFromCopy] public int DefaultImplementationIndex = -1; // if >= 0, then this is a default implementation

				// Default implementation helpers: system used to check if a variable is different than the default one (highlight labels)
				protected bool IsDefaultImplementation { get { return DefaultImplementationIndex >= 0; } }
				protected T GetDefaultImplementation<T>() where T : Implementation
				{
					return ParentShaderProperty.defaultImplementations[DefaultImplementationIndex] as T;
				}

				public ShaderProperty ParentShaderProperty;

				public virtual void CheckErrors() { }
				public virtual bool HasErrors { get { return false; } }

				public Implementation(ShaderProperty shaderProperty)
				{
					this.guid = Guid.NewGuid().ToString();

					if (shaderProperty != null)
					{
						ParentShaderProperty = shaderProperty;
						Label = shaderProperty.Name;
					}
				}

				// Defines if the implementation can be copied
				internal virtual bool CanBeCopied() { return true; }
				public virtual void WillBeRemoved() { }
				public virtual void OnPasted() { }

				public override string ToString()
				{
					return string.Format("[Implementation: {0}]", this.GetType());
				}

				public virtual string ToHashString()
				{
					var result = new StringBuilder();

					var props = GetType().GetProperties();
					foreach (var prop in props)
					{
						var attributes = prop.GetCustomAttributes(typeof(Serialization.SerializeAsAttribute), true);
						if (attributes == null || attributes.Length == 0)
						{
							continue;
						}

						if (prop.PropertyType == typeof(ShaderProperty))
						{
							var spRef = (ShaderProperty)prop.GetValue(this, null);
							result.Append(spRef != null ? spRef.Name : "EmptyShaderPropertyRef");
						}
						else
						{
							result.Append(prop.GetValue(this, null));
						}
					}

					var fields = GetType().GetFields();
					foreach (var field in fields)
					{
						if (field.Name == "guid") continue;
						result.Append(field.GetValue(this));
					}

					return result.ToString();
				}

				string DebugSerializableProps()
				{
					var result = new StringBuilder();

					var props = GetType().GetProperties();
					foreach (var prop in props)
					{
						var attributes = prop.GetCustomAttributes(typeof(Serialization.SerializeAsAttribute), true);
						if (attributes == null || attributes.Length == 0)
						{
							continue;
						}

						if (prop.PropertyType == typeof(ShaderProperty))
						{
							var spRef = (ShaderProperty)prop.GetValue(this, null);
							result.AppendLine(prop.Name + " = " + spRef != null ? spRef.Name : "EmptyShaderPropertyRef");
						}
						else
						{
							result.AppendLine(prop.Name + " = " + prop.GetValue(this, null));
						}
					}

					var fields = GetType().GetFields();
					foreach (var field in fields)
					{
						if (field.Name == "guid") continue;
						result.AppendLine(field.Name + " = " + field.GetValue(this));
					}

					return result.ToString();
				}

				void CompareToDefaultImplementation<T>() where T:Implementation
				{
					string current = DebugSerializableProps();
					string def = GetDefaultImplementation<T>().DebugSerializableProps();
					Debug.Log("Default:\n" + def);
					Debug.Log("Current:\n" + current);
				}

				internal Implementation CloneForNewShaderProperty(ShaderProperty sp, string suffix)
				{
					var clone = this.Clone(suffix);
					clone.guid = new Guid().ToString();
					clone.ParentShaderProperty = sp;
					return clone;
				}
				
				virtual public Implementation Clone(string suffix = null)
				{
					return (Implementation)MemberwiseClone();
				}

				public void CopyCommonProperties(Implementation from)
				{
					this.@operator = from.@operator;
					this.Label = from.Label;
					this.IsGpuInstanced = from.IsGpuInstanced;
					this.IsDotsInstanced = from.IsDotsInstanced;

					var from_mp = from as Imp_MaterialProperty;
					var this_mp = this as Imp_MaterialProperty;
					if (this_mp != null && from_mp != null)
					{
						this_mp.PropertyName = from_mp.PropertyName;
					}
				}

				public void SetOperator(object @operator)
				{
					this.@operator = (Operator)(Mathf.Clamp((int)@operator, 0, OperatorSymbols.Length));
				}

				//Label to show on the drop-down button when this implementation is selected
				internal virtual string GUILabel() { return "Error: base Implementation class"; }

				//Shader code output that goes in the ShaderLab Properties { } block
				internal virtual string PrintProperty(string indent) { return null; }

				//Shader code output that declares the variables, if any
				internal virtual string PrintVariableDeclare(string indent) { return null; }

				//Shader code output that declares the variables that are incompatible with CBUFFER blocks
				internal virtual string PrintVariableDeclareOutsideCBuffer(string indent) { return null; }

				internal virtual string PrintVariableDeclare(string indent, bool gpuInstanced)
				{
					// Default behavior for GPU instancing: print declaration only if flags match
					if ( (this.IsGpuInstanced && gpuInstanced) || (!this.IsGpuInstanced && !gpuInstanced) )
					{
						return PrintVariableDeclare(indent);
					}
					else
					{
						return null;
					}
				}

				//Shader code output that represents the variable in the fragment shader
				internal virtual string PrintVariableFragment(string inputSource, string outputSource, string arguments) { return null; }

				//shader code output that represents the variable in the vertex shader
				internal virtual string PrintVariableVertex(string inputSource, string outputSource, string arguments) { return PrintVariableFragment(inputSource, outputSource, arguments); }

				//output the value of a fixed function property: either a constant value, or a material property
				internal virtual string PrintVariableFixedFunction() { throw new InvalidOperationException("This implementation cannot be used with fixed function properties."); }

				//Returns a list of features needed to make this implementation work, such as USE_VERTEX_COLORS (enum)
				internal virtual OptionFeatures[] NeededFeatures() { return new OptionFeatures[0]; }

				//Returns a list of extra features needed to make this implementation work (raw strings)
				internal virtual string[] NeededFeaturesExtra() { return new string[0]; }

				//GUI that goes on the line(s) under the drop-down
				internal virtual void NewLineGUI(bool usedByCustomCode) { }

				internal virtual bool HasOperator() { return true; }

				protected static void BeginHorizontal(float indentOffset = 0f)
				{
					GUILayout.BeginHorizontal();
					GUILayout.Space(UI.GUI_NEWLINE_INDENT + indentOffset);
				}

				protected static void EndHorizontal()
				{
					GUILayout.Space(4);
					GUILayout.EndHorizontal();
				}

				public string PrintOperator()
				{
					switch (@operator)
					{
						case Operator.Multiply: return " * ";
						case Operator.Divide: return " / ";
						case Operator.Add: return " + ";
						case Operator.Subtract: return " - ";

						default:
							Debug.LogError(ShaderGenerator2.ErrorMsg("Unknown operator: " + @operator));
							return "";
					}
				}
			}

			[Serialization.SerializeAs("imp_hook")]
			public class Imp_Hook : Implementation
			{
				internal override bool CanBeCopied() { return false; }

				public static VariableType VariableCompatibility { get { return VariableTypeAll; } }
				public static string MenuLabel { get { return "Hook"; } }
				internal override string GUILabel() { return MenuLabel; }

				public Imp_Hook(ShaderProperty shaderProperty) : base(shaderProperty)
				{
				}

				internal override string PrintVariableFragment(string inputSource, string outputSource, string arguments)
				{
					return this.Label;
				}

				internal override void NewLineGUI(bool usedByCustomCode)
				{
					BeginHorizontal();
					{
						SGUILayout.InlineLabel("Shader Variable:");
						using (new EditorGUI.DisabledScope(true))
						{
							SGUILayout.TextField(this.Label);
						}
					}
					EndHorizontal();

					BeginHorizontal();
					{
						TCP2_GUI.HelpBoxLayout("Add implementations to this Shader Property to modify the output of this variable in the shader.", MessageType.Info);
					}
					EndHorizontal();
				}
			}

			public abstract class Imp_MaterialProperty : Implementation, IMaterialPropertyName
			{
				//system to ensure each property name is unique
				public string GetPropertyName() { return PropertyName; }

				[Serialization.SerializeAs("prop"), ExcludeFromCopy] public string _PropertyName = "_ShaderProperty";
				public string PropertyName
				{
					get
					{
						if (ParentShaderProperty.layerCloneSuffix != null)
						{
							return string.Format("{0}_{1}", _PropertyName, ParentShaderProperty.layerCloneSuffix);
						}

						return _PropertyName;
					}
					set
					{
						// ensure we get the actual name from the template if the variable name is locked:
						if (PropertyNameLocked)
						{
							_PropertyName = value;
							return;
						}
						
						_PropertyName = UniqueMaterialPropertyName.GetUniquePropertyName(value, this);
					}
				}
				[Serialization.SerializeAs("md")] public string MaterialDrawers = "";
				[Serialization.SerializeAs("gbv")] public bool IsGlobalVariable = false;
				[Serialization.SerializeAs("custom")] public bool IsCustomMaterialProperty = false;
				[Serialization.SerializeAs("refs")] public string CustomMaterialPropertyReferences = "";
				[Serialization.SerializeAs("pnlock")] public bool PropertyNameLocked = false;

				public Imp_MaterialProperty(ShaderProperty shaderProperty) : base(shaderProperty) { }

				public override Implementation Clone(string suffix = null)
				{
					var mp = (Imp_MaterialProperty)base.Clone(suffix);
					if (suffix == null)
					{
						//special case for material property: this will trigger the unique variable name check
						mp.PropertyName = mp.PropertyName;
					}
					else
					{
						// append suffix for the Label & PropertyName
						mp.PropertyName = string.Format("{0}_{1}", mp.PropertyName, suffix);
					}
					return mp;
				}

				public override void CheckErrors()
				{
					if (IsCustomMaterialProperty)
					{
						IsCustomMaterialPropertyReferenced();
					}

					base.CheckErrors();
				}

				// Used if we know the implementation will be deleted, so that its name is not taken into account for uniqueness
				public bool ignoreUniquePropertyName;

				protected bool IsCustomMaterialPropertyReferenced()
				{
					if (!IsCustomMaterialProperty)
					{
						throw new Exception("'IsCustomMaterialPropertyReferenced' shouldn't be used when 'IsCustomMaterialProperty' is false");
					}

					bool isReferenced = false;
					CustomMaterialPropertyReferences = "";
					foreach (var sp in ShaderGenerator2.CurrentConfig.VisibleShaderProperties)
					{
						foreach (var imp in sp.implementations)
						{
							var imp_cmp = imp as Imp_CustomMaterialProperty;
							if (imp_cmp != null && !imp_cmp.willBeRemoved)
							{
								if (imp_cmp.LinkedCustomMaterialProperty != null && imp_cmp.LinkedCustomMaterialProperty.implementation == this)
								{
									isReferenced = true;
									if (!CustomMaterialPropertyReferences.Contains(imp_cmp.ParentShaderProperty.DisplayName))
									{
										CustomMaterialPropertyReferences += imp_cmp.ParentShaderProperty.DisplayName + ", ";
									}
								}
							}

							var imp_cc = imp as Imp_CustomCode;
							if (imp_cc != null)
							{
								if (imp_cc.code.Contains(this.PropertyName) || imp_cc.prependCode.Contains(this.PropertyName))
								{
									CustomMaterialPropertyReferences += imp_cc.ParentShaderProperty.DisplayName + ", ";
								}
							}
						}
					}

					if (CustomMaterialPropertyReferences.Length > 0)
					{
						CustomMaterialPropertyReferences = CustomMaterialPropertyReferences.Substring(0, CustomMaterialPropertyReferences.Length-2); // remove trailing ", "
					}

					return isReferenced;
				}

				protected abstract string PropertyTypeName();

				protected string FetchVariable(string variableName, bool ignoreLayer = false)
				{
					if (!ignoreLayer && ParentShaderProperty.layerCloneSuffix != null)
					{
						variableName = string.Format("{0}_{1}", variableName, ParentShaderProperty.layerCloneSuffix);
					}
					
					return this.IsGpuInstanced ? string.Format("UNITY_ACCESS_INSTANCED_PROP(Props, {0})", variableName) : variableName;
				}

				internal override void NewLineGUI(bool usedByCustomCode)
				{
					BeginHorizontal();
					{
						ShaderGenerator2.ContextualHelpBox(string.Format("Will create a {0} property that you can tweak in the Material Inspector, or with scripts with the Material or Shader APIs.", PropertyTypeName()));
					}
					EndHorizontal();
					GUILayout.Space(4f);

					if (IsCustomMaterialProperty)
					{
						// Show references
						BeginHorizontal();
						{
							bool isReferenced = !string.IsNullOrEmpty(CustomMaterialPropertyReferences);
							string color = EditorGUIUtility.isProSkin ? "#00927C" : "#087566";
							string label = isReferenced ? "<b><color={0}>Referenced by:</color></b> " + CustomMaterialPropertyReferences :
								"<i>This Material Property isn't referenced in any Shader Property, it won't be included in the generated shader.</i>";

							GUILayout.Label(string.Format(label, color), SGUILayout.Styles.GrayMiniLabelWrap, GUILayout.ExpandWidth(true));
						}
						EndHorizontal();

						GUILayout.Space(5);
						BeginHorizontal();
						{
							GUILayout.Space(2);
							SGUILayout.DrawLine(EditorGUIUtility.isProSkin ? new Color(.3f, .3f, .3f) : new Color(.65f, .65f, .65f));
						}
						EndHorizontal();
						GUILayout.Space(5);
					}

					BeginHorizontal();
					{
						SGUILayout.InlineLabel("Label");
						Label = SGUILayout.TextField(Label);
					}
					EndHorizontal();

					BeginHorizontal();
					{
						SGUILayout.InlineLabel("Variable");

						Rect rect = SGUILayout.GetControlRect(SGUILayout.Styles.ShurikenValue);
						Rect buttonRect = rect;
						buttonRect.width = 18;
						rect.xMin += buttonRect.width + 2;

						using (new EditorGUI.DisabledScope(PropertyNameLocked))
						{
							if (GUI.Button(buttonRect, TCP2_GUI.TempContent(">", "Generate from Label"), SGUILayout.Styles.ShurikenMiniButtonFlexible))
							{
								PropertyName = string.Format("_{0}", this.Label);
								//ShaderGenerator2.PushUndoState();
							}

							var newName = SGUILayout.TextFieldShaderVariable(rect, PropertyName);
							if (newName != PropertyName)
							{
								// Only update if value is effectively changed, because we're calling a setter that loops through all ShaderProperties
								PropertyName = newName;
							}
						}
					}
					EndHorizontal();

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? IsGlobalVariable : IsGlobalVariable != GetDefaultImplementation<Imp_MaterialProperty>().IsGlobalVariable;
						SGUILayout.InlineLabel("Global Variable", "Make this variable global so that it can be changed through scripts, e.g. with 'Shader.SetGlobalColor'", highlighted);
						IsGlobalVariable = SGUILayout.Toggle(IsGlobalVariable);
					}
					EndHorizontal();

					BeginHorizontal();
					{
						using (new EditorGUI.DisabledScope(IsGlobalVariable))
						{
							bool highlighted = !IsDefaultImplementation ? !string.IsNullOrEmpty(MaterialDrawers) : MaterialDrawers != GetDefaultImplementation<Imp_MaterialProperty>().MaterialDrawers;
							SGUILayout.InlineLabel("Property Drawers", "Add one or multiple property drawers/decorators to this property\n(e.g. [NoScaleOffset])", highlighted);
							MaterialDrawers = SGUILayout.TextField(MaterialDrawers);
						}
					}
					EndHorizontal();

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? IsGpuInstanced : IsGpuInstanced != GetDefaultImplementation<Imp_MaterialProperty>().IsGpuInstanced;
						SGUILayout.InlineLabel("GPU Instanced", "Tag this property as a possible variant for GPU instancing", highlighted);
						EditorGUI.BeginChangeCheck();
						IsGpuInstanced = SGUILayout.Toggle(IsGpuInstanced);
						if (EditorGUI.EndChangeCheck())
							if (IsDotsInstanced && IsGpuInstanced)
								IsDotsInstanced = false;
					}
					EndHorizontal();

					if (ShaderGenerator2.IsURP)
					{
						BeginHorizontal();
						{
							bool highlighted = !IsDefaultImplementation ? IsDotsInstanced : IsDotsInstanced != GetDefaultImplementation<Imp_MaterialProperty>().IsDotsInstanced;
							SGUILayout.InlineLabel("DOTS/BRG Instanced", "Tag this property as supporting BatchRendererGroup instancing (DOTS, GPU Resident Drawer). BRG Instancing must also be enabled in the FEATURES tab.", highlighted);
							EditorGUI.BeginChangeCheck();
							IsDotsInstanced = SGUILayout.Toggle(IsDotsInstanced);
							if (EditorGUI.EndChangeCheck())
								if (IsDotsInstanced && IsGpuInstanced)
									IsGpuInstanced = false;
						}
						EndHorizontal();
					}

					BeginHorizontal();
					GUILayout.Space(2);
					SGUILayout.DrawLine(EditorGUIUtility.isProSkin ? new Color(.3f, .3f, .3f) : new Color(.65f, .65f, .65f));
					EndHorizontal();
					GUILayout.Space(5);
				}

				internal override string PrintProperty(string indent)
				{
					if (IsGlobalVariable)
					{
						return "";
					}

					return PrintPropertyInternal(indent);
				}
				
				internal virtual string PrintPropertyInternal(string indent)
				{
					return MaterialDrawers + " ";
				}

				internal override string PrintVariableDeclare(string indent)
				{
					if (IsGlobalVariable)
						return null;
					return PrintVariableInternal(indent);
				}

				internal override string PrintVariableDeclareOutsideCBuffer(string indent)
				{
					if (!IsGlobalVariable)
						return null;
					return PrintVariableInternal(indent);
				}

				protected abstract string PrintVariableInternal(string indent);
			}

			[Serialization.SerializeAs("imp_mp_float")]
			public class Imp_MaterialProperty_Float : Imp_MaterialProperty
			{
				public static VariableType VariableCompatibility { get { return VariableTypeAll | VariableType.fixed_function_float; } }
				public static string MenuLabel { get { return "Material Property/Float"; } }
				internal override string GUILabel() { return MenuLabel; }
				protected override string PropertyTypeName() { return "float"; }

				[Serialization.SerializeAs("def")] public float DefaultValue = 1.0f;

				public Imp_MaterialProperty_Float(ShaderProperty shaderProperty) : base(shaderProperty)
				{
					PropertyName = "_" + SGUILayout.Utils.RemoveWhitespaces(Label);
					Label = shaderProperty.Name + " Float";
				}

				internal override string PrintVariableFixedFunction() { return string.Format("[{0}]", PropertyName); }
				internal override string PrintPropertyInternal(string indent) { return base.PrintPropertyInternal(indent) + string.Format(CultureInfo.InvariantCulture, "{0} (\"{1}\", Float) = {2}", PropertyName, Label, DefaultValue); }
				protected override string PrintVariableInternal(string indent) { return string.Format("{0}float {1};", indent, PropertyName); }
				internal override string PrintVariableFragment(string inputSource, string outputSource, string arguments) { return FetchVariable(PropertyName, true); }

				internal override void NewLineGUI(bool usedByCustomCode)
				{
					base.NewLineGUI(usedByCustomCode);

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? DefaultValue != 1.0f : DefaultValue != GetDefaultImplementation<Imp_MaterialProperty_Float>().DefaultValue;
						SGUILayout.InlineLabel("Default Value", highlighted);
						DefaultValue = SGUILayout.FloatField(DefaultValue);
					}
					EndHorizontal();
				}
			}

			[Serialization.SerializeAs("imp_mp_range")]
			public class Imp_MaterialProperty_Range : Imp_MaterialProperty
			{
				public static VariableType VariableCompatibility { get { return VariableTypeAll | VariableType.fixed_function_float; } }
				public static string MenuLabel { get { return "Material Property/Range"; } }
				internal override string GUILabel() { return MenuLabel; }
				protected override string PropertyTypeName() { return "float range"; }

				[Serialization.SerializeAs("def")] public float DefaultValue = 0.5f;
				[Serialization.SerializeAs("min")] public float Min;
				[Serialization.SerializeAs("max")] public float Max = 1.0f;

				public Imp_MaterialProperty_Range(ShaderProperty shaderProperty) : base(shaderProperty)
				{
					PropertyName = "_" + SGUILayout.Utils.RemoveWhitespaces(Label);
					Label = shaderProperty.Name + " Range";
				}

				internal override string PrintVariableFixedFunction() { return string.Format("[{0}]", PropertyName); }
				internal override string PrintPropertyInternal(string indent) { return base.PrintPropertyInternal(indent) + string.Format(CultureInfo.InvariantCulture, "{0} (\"{1}\", Range({3},{4})) = {2}", PropertyName, Label, DefaultValue, Min, Max); }
				protected override string PrintVariableInternal(string indent) { return string.Format("{0}float {1};", indent, PropertyName); }
				internal override string PrintVariableFragment(string inputSource, string outputSource, string arguments) { return FetchVariable(PropertyName, true); }

				internal override void NewLineGUI(bool usedByCustomCode)
				{
					base.NewLineGUI(usedByCustomCode);

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? Min != 0.0f : Min != GetDefaultImplementation<Imp_MaterialProperty_Range>().Min;
						SGUILayout.InlineLabel("Min", highlighted);
						Min = SGUILayout.FloatField(Min);
					}
					EndHorizontal();

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? Max != 1.0f : Max != GetDefaultImplementation<Imp_MaterialProperty_Range>().Max;
						SGUILayout.InlineLabel("Max", highlighted);
						Max = SGUILayout.FloatField(Max);
					}
					EndHorizontal();

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? DefaultValue != 0.5f : DefaultValue != GetDefaultImplementation<Imp_MaterialProperty_Range>().DefaultValue;
						SGUILayout.InlineLabel("Default Value", highlighted);
						DefaultValue = SGUILayout.FloatField(DefaultValue);
					}
					EndHorizontal();
				}
			}

			[Serialization.SerializeAs("imp_mp_vector")]
			public class Imp_MaterialProperty_Vector : Imp_MaterialProperty
			{
				public static VariableType VariableCompatibility { get { return VariableType.float2 | VariableType.float3 | VariableType.float4 | VariableType.color | VariableType.color_rgba; } }
				public static string MenuLabel { get { return "Material Property/Vector"; } }
				internal override string GUILabel() { return MenuLabel; }
				protected override string PropertyTypeName() { return "vector4"; }

				[Serialization.SerializeAs("def")] public Vector4 DefaultValue = Vector4.zero;
				[Serialization.SerializeAs("fp")] public FloatPrecision FloatPrec;
				[Serialization.SerializeAs("cc")] public int ChannelsCount = 3;
				[Serialization.SerializeAs("chan")] public string Channels = "XYZ";
				string DefaultChannels = "RGBA";

				public Imp_MaterialProperty_Vector(ShaderProperty shaderProperty) : base(shaderProperty)
				{
					PropertyName = "_" + SGUILayout.Utils.RemoveWhitespaces(Label);
					Label = shaderProperty.Name + " Vector";

					InitChannelsCount();
					InitChannelsSwizzle();
				}

				void InitChannelsCount()
				{
					switch (ParentShaderProperty.Type)
					{
						case VariableType.float2: ChannelsCount = 2; break;
						case VariableType.color:
						case VariableType.float3: ChannelsCount = 3; break;
						case VariableType.color_rgba:
						case VariableType.float4: ChannelsCount = 4; break;
					}
				}

				void InitChannelsSwizzle()
				{
					switch (ParentShaderProperty.Type)
					{
						case VariableType.float2: Channels = "XY"; break;
						case VariableType.color:
						case VariableType.float3: Channels = "XYZ"; break;
						case VariableType.color_rgba:
						case VariableType.float4: Channels = "XYZW"; break;
					}
					DefaultChannels = Channels;
				}

				public override void OnPasted()
				{
					InitChannelsCount();
				}

				internal override string PrintPropertyInternal(string indent) { return base.PrintPropertyInternal(indent) + string.Format(CultureInfo.InvariantCulture, "{0} (\"{1}\", Vector) = ({2},{3},{4},{5})", PropertyName, Label, DefaultValue.x, DefaultValue.y, DefaultValue.z, DefaultValue.w); }
				protected override string PrintVariableInternal(string indent)
				{
					// Always declare a float4, even if all channels aren't necessarily used, as they could still be used for custom code
					//var channels = ChannelsCount > 1 ? ChannelsCount.ToString() : "";
					string channels = "4";
					return string.Format("{0}{1}{2} {3};", indent, FloatPrec, channels, PropertyName);
				}
				internal override string PrintVariableFragment(string inputSource, string outputSource, string arguments)
				{
					var hideChannels = TryGetArgument("hide_channels", arguments);
					var channels = string.IsNullOrEmpty(hideChannels) ? "." + Channels.ToLowerInvariant() : "";
					return string.Format("{0}{1}", FetchVariable(PropertyName, true), channels);
				}

				internal override void NewLineGUI(bool usedByCustomCode)
				{
					base.NewLineGUI(usedByCustomCode);

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? DefaultValue != Vector4.zero : DefaultValue != GetDefaultImplementation<Imp_MaterialProperty_Vector>().DefaultValue;
						SGUILayout.InlineLabel("Default Value", highlighted);
						int channelsCount = usedByCustomCode ? 4 : ChannelsCount;
						switch (channelsCount)
						{
							case 4: DefaultValue = SGUILayout.Vector4Field(DefaultValue); break;
							case 3: DefaultValue = SGUILayout.Vector3Field(DefaultValue); break;
							case 2: DefaultValue = SGUILayout.Vector2Field(DefaultValue); break;
						}
					}
					EndHorizontal();

					if (!IsCustomMaterialProperty)
					{
						BeginHorizontal();
						{
							bool highlighted = !IsDefaultImplementation ? Channels != DefaultChannels : Channels != GetDefaultImplementation<Imp_MaterialProperty_Vector>().Channels;
							SGUILayout.InlineLabel("Swizzle", highlighted);

							if (usedByCustomCode)
							{
								using (new EditorGUI.DisabledScope(true))
								{
									GUILayout.Label(TCP2_GUI.TempContent("Defined in Custom Code"), SGUILayout.Styles.ShurikenValue, GUILayout.Height(16), GUILayout.ExpandWidth(false));
								}
							}
							else
							{
								if (ChannelsCount == 1)
									Channels = SGUILayout.XYZWSelector(Channels);
								else
									Channels = SGUILayout.XYZWSwizzle(Channels, ChannelsCount);
							}
						}
						EndHorizontal();
					}
				}
			}

			[Serialization.SerializeAs("imp_mp_color")]
			public class Imp_MaterialProperty_Color : Imp_MaterialProperty
			{
				public static VariableType VariableCompatibility { get { return VariableType.float2 | VariableType.float3 | VariableType.float4 | VariableType.color | VariableType.color_rgba; } }
				public static string MenuLabel { get { return "Material Property/Color"; } }
				internal override string GUILabel() { return MenuLabel; }
				protected override string PropertyTypeName() { return "color"; }

				[Serialization.SerializeAs("def")] public Color DefaultValue = Color.white;
				[Serialization.SerializeAs("hdr")] public bool Hdr;
				[Serialization.SerializeAs("cc")] public int ChannelsCount = 4;
				[Serialization.SerializeAs("chan")] public string Channels = "RGB";
				string DefaultChannels = "RGB";

				public Imp_MaterialProperty_Color(ShaderProperty shaderProperty) : base(shaderProperty)
				{
					PropertyName = "_" + SGUILayout.Utils.RemoveWhitespaces(Label);
					Label = shaderProperty.Name + " Color";

					InitChannelsCount();
					InitChannelsSwizzle();
				}

				void InitChannelsCount()
				{
					switch (ParentShaderProperty.Type)
					{
						case VariableType.float2: ChannelsCount = 2; break;
						case VariableType.color:
						case VariableType.float3: ChannelsCount = 3; break;
						case VariableType.color_rgba:
						case VariableType.float4: ChannelsCount = 4; break;
					}
				}

				void InitChannelsSwizzle()
				{
					switch (ParentShaderProperty.Type)
					{
						case VariableType.float2: Channels = "RG"; break;
						case VariableType.color:
						case VariableType.float3: Channels = "RGB"; break;
						case VariableType.color_rgba:
						case VariableType.float4: Channels = "RGBA"; break;
					}
					DefaultChannels = Channels;
				}

				public override void OnPasted()
				{
					InitChannelsCount();
				}

				internal override string PrintPropertyInternal(string indent) { return base.PrintPropertyInternal(indent) + string.Format(CultureInfo.InvariantCulture, "{7}{6}{0} (\"{1}\", Color) = ({2},{3},{4},{5})", PropertyName, Label, DefaultValue.r, DefaultValue.g, DefaultValue.b, DefaultValue.a, Hdr ? "[HDR] " : "", ChannelsCount < 4 ? "[TCP2ColorNoAlpha] " : ""); }
				protected override string PrintVariableInternal(string indent)
				{
					// Always declare a float4, even if all channels aren't necessarily used, as they could still be used for custom code
					//var channels = ChannelsCount > 1 ? ChannelsCount.ToString() : "";
					string channels = "4";
					return string.Format("{0}{1}{2} {3};", indent, Hdr ? FloatPrecision.half : FloatPrecision.@fixed, channels, PropertyName);
				}
				internal override string PrintVariableFragment(string inputSource, string outputSource, string arguments)
				{
					var hideChannels = TryGetArgument("hide_channels", arguments);
					var channels = string.IsNullOrEmpty(hideChannels) ? "." + Channels.ToLowerInvariant() : "";
					return string.Format("{0}{1}", FetchVariable(PropertyName, true), channels);
				}

				internal override void NewLineGUI(bool usedByCustomCode)
				{
					base.NewLineGUI(usedByCustomCode);

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? DefaultValue != Color.white : DefaultValue != GetDefaultImplementation<Imp_MaterialProperty_Color>().DefaultValue;
						SGUILayout.InlineLabel("Default Value", highlighted);
						var showAlpha = ChannelsCount >= 4 || usedByCustomCode;
						DefaultValue = SGUILayout.ColorField(DefaultValue, showAlpha, Hdr);
					}
					EndHorizontal();

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? Hdr : Hdr != GetDefaultImplementation<Imp_MaterialProperty_Color>().Hdr;
						SGUILayout.InlineLabel("HDR Color", highlighted);
						Hdr = SGUILayout.Toggle(Hdr);
					}
					EndHorizontal();

					if (!IsCustomMaterialProperty)
					{
						BeginHorizontal();
						{
							bool highlighted = !IsDefaultImplementation ? Channels != DefaultChannels : Channels != GetDefaultImplementation<Imp_MaterialProperty_Color>().Channels;
							SGUILayout.InlineLabel("Swizzle", highlighted);

							if (usedByCustomCode)
							{
								using (new EditorGUI.DisabledScope(true))
								{
									GUILayout.Label(TCP2_GUI.TempContent("Defined in Custom Code"), SGUILayout.Styles.ShurikenValue, GUILayout.Height(16), GUILayout.ExpandWidth(false));
								}
							}
							else
							{
								if (ChannelsCount == 1)
									Channels = SGUILayout.RGBASelector(Channels);
								else
									Channels = SGUILayout.RGBASwizzle(Channels, ChannelsCount);
							}
						}
						EndHorizontal();
					}
				}
			}

			[Serialization.SerializeAs("imp_mp_texture")]
			public class Imp_MaterialProperty_Texture : Imp_MaterialProperty
			{
				public static VariableType VariableCompatibility { get { return VariableTypeAll; } }
				public static string MenuLabel { get { return "Material Property/Texture"; } }
				internal override string GUILabel() { return MenuLabel; }
				protected override string PropertyTypeName() { return "texture"; }

				public override bool HasErrors
				{
					get
					{
						bool linkedSpErrors = UvSource == UvSourceType.OtherShaderProperty &&
							( _linkedShaderProperty == null || (_linkedShaderProperty != null && !_linkedShaderProperty.IsVisible()) );

						bool linkedCmpErrors = UvSource == UvSourceType.CustomMaterialProperty && _linkedCustomMaterialProperty == null;
						
						return base.HasErrors
						       | linkedSpErrors
						       | linkedCmpErrors
						       | (UseTilingOffset && invalidTilingOffsetVariable)
						       | (UseScrolling && invalidScrollingVariable)
						       | (SineAnimation && invalidSinAnimVariable)
						       | InvalidSampler;
					}
				}

				public override void CheckErrors()
				{
					base.CheckErrors();

					VerifyReferencedValuesValidity();
					VerifySamplerValidity();
				}

				internal override OptionFeatures[] NeededFeatures()
				{
					List<OptionFeatures> list = new List<OptionFeatures>();

					list.AddRange(base.NeededFeatures());

					if (NoTile)
					{
						if (program == ProgramType.Fragment)
						{
							list.Add(OptionFeatures.NoTile_Sampling);
						}
						else if (program == ProgramType.Vertex)
						{
							list.Add(OptionFeatures.NoTile_Sampling_Vertex);
						}
					}

					if (UvSource == UvSourceType.Triplanar)
					{
						if (program == ProgramType.Vertex)
						{
							list.Add(OptionFeatures.Triplanar_Sampling_Vertex);
						}
						else
						{
							list.Add(OptionFeatures.Triplanar_Sampling);

							if (LocalSpaceTriplanar)
							{
								list.Add(OptionFeatures.Triplanar_Sampling_Local);
							}
							else
							{
								list.Add(OptionFeatures.Triplanar_Sampling_Global);
							}
						}
					}

					if (RandomOffset)
					{
						list.Add(OptionFeatures.UV_Anim_Random_Offset);
					}

					if (SineAnimation)
					{
						if (UvSource == UvSourceType.WorldPosition)
						{
							list.Add(OptionFeatures.UV_Anim_Sine_World);
						}
						else
						{
							list.Add(OptionFeatures.UV_Anim_Sine);
						}
					}

					if (UvSource == UvSourceType.ScreenSpace)
					{
						list.Add(ScreenSpaceUVVertex ? OptionFeatures.Screen_Space_UV_Vertex : OptionFeatures.Screen_Space_UV_Fragment);

						if (ScreenSpaceUVObjectOffset && !ScreenSpaceUVVertex)
						{
							list.Add(OptionFeatures.Screen_Space_UV_Object_Offset);
						}
					}

					if (UvSource == UvSourceType.WorldPosition)
					{
						list.Add((program == ProgramType.Vertex) ? OptionFeatures.World_Pos_UV_Vertex : OptionFeatures.World_Pos_UV_Fragment);
					}

					return list.ToArray();
				}

				public enum UvSourceType
				{
					Texcoord,
					ScreenSpace,
					WorldPosition,
					OtherShaderProperty,
					Triplanar,
					CustomMaterialProperty
				}

				[Serialization.SerializeAs("uto")] public bool UseTilingOffset;
				[Serialization.SerializeAs("tov")] public string TilingOffsetVariable = "";
				[Serialization.SerializeAs("tov_lbl")] public string TilingOffsetVariableLabel = "";
				[Serialization.SerializeAs("gto")] public bool GlobalTilingOffset;
				[Serialization.SerializeAs("sbt")] public bool ScaleByTexelSize;
				[Serialization.SerializeAs("scr")] public bool UseScrolling;
				[Serialization.SerializeAs("scv")] public string ScrollingVariable = "";
				[Serialization.SerializeAs("scv_lbl")] public string ScrollingVariableLabel = "";
				[Serialization.SerializeAs("gsc")] public bool GlobalScrolling;
				[Serialization.SerializeAs("roff")] public bool RandomOffset;
				[Serialization.SerializeAs("goff")] public bool GlobalRandomOffset;
				[Serialization.SerializeAs("sin_anm")] public bool SineAnimation;
				[Serialization.SerializeAs("sin_anmv")] public string SineAnimationVariable = "";
				[Serialization.SerializeAs("sin_anmv_lbl")] public string SineAnimationVariableLabel = "";
				[Serialization.SerializeAs("gsin")] public bool GlobalSineAnimation;
				[Serialization.SerializeAs("notile")] public bool NoTile;
				[Serialization.SerializeAs("triplanar_local")] public bool LocalSpaceTriplanar;
				[Serialization.SerializeAs("def")] public string DefaultValue = SGUILayout.Constants.DefaultTextureValues[0];
				[Serialization.SerializeAs("locked_uv"), ExcludeFromCopy] public bool IsUvLocked;
				[Serialization.SerializeAs("uv")] public int UvChannel;
				[Serialization.SerializeAs("cc")] public int ChannelsCount = 3;
				[Serialization.SerializeAs("chan")] public string Channels = "RGB";
				[Serialization.SerializeAs("mip")] public int MipLevel = -1;
				[Serialization.SerializeAs("mipprop")] public bool MipProperty;
				//[Serialization.SerializeAs("ssuv")] public bool UseScreenSpaceUV;
				[Serialization.SerializeAs("ssuv_vert")] public bool ScreenSpaceUVVertex;
				[Serialization.SerializeAs("ssuv_obj")] public bool ScreenSpaceUVObjectOffset;
				//[Serialization.SerializeAs("wpuv")] public bool UseWorldPosUV;
				[Serialization.SerializeAs("uv_type")] public UvSourceType UvSource = UvSourceType.Texcoord;
				[Serialization.SerializeAs("uv_chan")] public string UVChannels = "XZ";
				[Serialization.SerializeAs("tpln_scale")] public float UVTriplanarScale = 1.0f;
				[Serialization.SerializeAs("uv_shaderproperty")] public string LinkedShaderPropertyName;
				[Serialization.SerializeAs("uv_cmp")] public string LinkedCustomMaterialPropertyName;
				string UvChannelsOptions = "XYZ";
				
				// Allow reusing samplers from other textures
				// Only works with Unity 2019.4+ due to bugs with Surface Shaders prior to that version
#if UNITY_2019_4_OR_NEWER
				[Serialization.SerializeAs("sep_sampler")] public string SeparateSamplerName;
				internal int SamplerGroup;
				bool InvalidSampler;
				bool UseSeparateSampler { get { return SeparateSamplerName != null && CanUseSeparateSampler && !UseOldSampler2DSyntax; } }
				bool UseOldSampler2DSyntax { get { return !ShaderGenerator2.IsURP && (NoTile || UvSource == UvSourceType.Triplanar); }}
				bool CanUseSeparateSampler { get { return ShaderGenerator2.IsURP || !(NoTile || UvSource == UvSourceType.Triplanar); } }
#else
				bool InvalidSampler
				{
					get { return false; }
				}
#endif

				// ------------------------------------------------------------------------------------------------
				// UV Other Shader Property mode

				ShaderProperty _linkedShaderProperty;
				public ShaderProperty LinkedShaderProperty
				{
					get { return _linkedShaderProperty; }
					set
					{
						SetLinkedShaderProperty(value);
					}
				}
				public List<ShaderProperty> Dependencies = new List<ShaderProperty>();

				public void TryToFindLinkedShaderProperty()
				{
					if (string.IsNullOrEmpty(LinkedShaderPropertyName))
					{
						return;
					}

					if (ShaderGenerator2.CurrentConfig == null)
					{
						return;
					}

					var match = Array.Find(ShaderGenerator2.CurrentConfig.VisibleShaderProperties, sp => sp.Name == LinkedShaderPropertyName);
					if (match != null)
					{
						SetLinkedShaderProperty(match);
					}
				}

				void SetLinkedShaderProperty(ShaderProperty shaderProperty)
				{
					if (shaderProperty == LinkedShaderProperty)
						return;

					if (shaderProperty == ParentShaderProperty)
					{
						Debug.LogError(ShaderGenerator2.ErrorMsg("Shader Property Referenced implementation tried to reference its parent: '" + shaderProperty.Name + "'"));
						return;
					}

					//build dependencies list to check cyclic references
					Dependencies.Clear();
					foreach (var imp in shaderProperty.implementations)
					{
						var impSpRef = imp as Imp_ShaderPropertyReference;
						if (impSpRef != null)
							Dependencies.AddRange(impSpRef.Dependencies);
					}
					if (Dependencies.Contains(shaderProperty))
					{
						//cyclic reference: can happen if a template has incorrect values
						Debug.LogError(ShaderGenerator2.ErrorMsg("Cyclic reference between '" + this.ParentShaderProperty.Name + "' and '" + shaderProperty.Name + "'"));
						return;
					}
					Dependencies.Add(shaderProperty);

					//assign as new linked shader property
					_linkedShaderProperty = shaderProperty;
					LinkedShaderPropertyName = _linkedShaderProperty == null ? "" : _linkedShaderProperty.Name;

					if (shaderProperty == null)
					{
						Debug.LogError(ShaderGenerator2.ErrorMsg("Referenced ShaderProperty is null"));
						return;
					}

					//determine default swizzle value based on channels count & linked shader property available channels
					bool sourceIsColor = shaderProperty.Type == VariableType.color || shaderProperty.Type == VariableType.color_rgba;
					string options = sourceIsColor ? "RGBA" : "XYZW";
					switch (shaderProperty.Type)
					{
						case VariableType.@float: UvChannelsOptions = "X"; break;
						case VariableType.float2: UvChannelsOptions = "XY"; break;
						case VariableType.float3: UvChannelsOptions = "XYZ"; break;
						case VariableType.float4: UvChannelsOptions = "XYZW"; break;
						case VariableType.color: UvChannelsOptions = "RGB"; break;
						case VariableType.color_rgba: UvChannelsOptions = "RGBA"; break;
					}

					// set default channels, or preserve existing ones as far as possible (the implementation could have just been deserialized)
					var prevChannels = UVChannels;
					UVChannels = "";
					for (int i = 0; i < 2; i++)
					{
						if (i < prevChannels.Length && options.Contains(prevChannels[i].ToString()))
							UVChannels += prevChannels[i];
						else
							UVChannels += options[i % options.Length];
					}
				}

				void OnSelectShaderProperty(object sp)
				{
					LinkedShaderProperty = sp as ShaderProperty;
					ParentShaderProperty.CheckHash();
					ShaderGenerator2.NeedsHashUpdate = true;
				}

				//Force updating the Shader Property hash once we've retrieved the correct Linked Shader Property
				public void ForceUpdateParentDefaultHash()
				{
					ParentShaderProperty.ForceUpdateDefaultHash();
				}

				// ------------------------------------------------------------------------------------------------
				// Custom Material Property UV Property mode

				CustomMaterialProperty _linkedCustomMaterialProperty;
				public CustomMaterialProperty LinkedCustomMaterialProperty
				{
					get { return _linkedCustomMaterialProperty; }
					set
					{
						SetLinkedCustomMaterialProperty(value);
					}
				}
				// public List<ShaderProperty> Dependencies = new List<ShaderProperty>();

				public void TryToFindLinkedCustomMaterialProperty()
				{
					if (string.IsNullOrEmpty(LinkedCustomMaterialPropertyName))
					{
						return;
					}

					if (ShaderGenerator2.CurrentConfig == null)
					{
						return;
					}

					var match = Array.Find(ShaderGenerator2.CurrentConfig.CustomMaterialProperties, cmp => cmp.PropertyName == LinkedCustomMaterialPropertyName);
					if (match != null)
					{
						SetLinkedCustomMaterialProperty(match);
					}
				}

				void SetLinkedCustomMaterialProperty(CustomMaterialProperty customMaterialProperty)
				{
					if (customMaterialProperty == LinkedCustomMaterialProperty)
						return;

					if (customMaterialProperty == null)
					{
						_linkedCustomMaterialProperty = null;
						LinkedCustomMaterialPropertyName = null;
						return;
					}
					
					//assign as new linked shader property
					_linkedCustomMaterialProperty = customMaterialProperty;
					LinkedCustomMaterialPropertyName = _linkedCustomMaterialProperty == null ? "" : _linkedCustomMaterialProperty.PropertyName;

					//determine default swizzle value based on channels count & linked shader property available channels
					bool sourceIsColor = customMaterialProperty.implementation is Imp_MaterialProperty_Color || customMaterialProperty.implementation is Imp_MaterialProperty_Texture; 
					string options = sourceIsColor ? "RGBA" : "XYZW";

					UvChannelsOptions = customMaterialProperty.Channels;

					// set default channels, or preserve existing ones as far as possible (the implementation could have just been deserialized)
					var prevChannels = UVChannels;
					UVChannels = "";
					for (int i = 0; i < 2; i++)
					{
						if (i < prevChannels.Length && options.Contains(prevChannels[i].ToString()))
							UVChannels += prevChannels[i];
						else
							UVChannels += options[i % options.Length];
					}
				}

				void OnSelectCustomMaterialProperty(object cmp)
				{
					LinkedCustomMaterialProperty = cmp as CustomMaterialProperty;
					ParentShaderProperty.CheckHash();
					ShaderGenerator2.NeedsHashUpdate = true;
				}

				// ------------------------------------------------------------------------------------------------

				string DefaultChannels = "RGB";

				ProgramType program = ProgramType.Undefined;
				bool invalidTilingOffsetVariable = false;
				bool invalidScrollingVariable = false;
				bool invalidSinAnimVariable = false;

				bool? _uvExpandedCache;
				bool uvExpandedCache
				{
					get
					{
						if(_uvExpandedCache == null)
						{
							_uvExpandedCache = ParentShaderProperty.implementationsExpandedStates.Contains(this.guid.GetHashCode());
						}
						return _uvExpandedCache.Value;
					}
				}

				public Imp_MaterialProperty_Texture(ShaderProperty shaderProperty) : base(shaderProperty)
				{
					program = shaderProperty != null ? shaderProperty.Program : ProgramType.Undefined;
					PropertyName = "_" + SGUILayout.Utils.RemoveWhitespaces(Label);
					Label = shaderProperty.Name + " Texture";

					InitChannelsCount();
					InitChannelsSwizzle();

					//make mip level accessible if vertex program
					if (shaderProperty != null && shaderProperty.Program == ProgramType.Vertex)
					{
						MipLevel = 0;
					}
				}

				void InitChannelsCount()
				{
					switch (ParentShaderProperty.Type)
					{
						case VariableType.@float: ChannelsCount = 1; break;
						case VariableType.float2: ChannelsCount = 2; break;
						case VariableType.color:
						case VariableType.float3: ChannelsCount = 3; break;
						case VariableType.color_rgba:
						case VariableType.float4: ChannelsCount = 4; break;
					}
				}

				void InitChannelsSwizzle()
				{
					switch (ParentShaderProperty.Type)
					{
						case VariableType.@float: Channels = "R"; break;
						case VariableType.float2: Channels = "RG"; break;
						case VariableType.color:
						case VariableType.float3: Channels = "RGB"; break;
						case VariableType.color_rgba:
						case VariableType.float4: Channels = "RGBA"; break;
					}
					DefaultChannels = Channels;
				}

				public override void OnPasted()
				{
					InitChannelsCount();
					TryToFindLinkedShaderProperty();
				}

				string GetMipValue()
				{
					return MipProperty ? FetchVariable(PropertyName + "_Mip", true) : MipLevel.ToString();
				}

				public void SetScreenSpaceUV()
				{
					UvSource = UvSourceType.ScreenSpace;
					var uvLabelArray = program == ProgramType.Vertex ? SGUILayout.Constants.UvChannelOptionsVertex : SGUILayout.Constants.UvChannelOptions;
					UvChannel = Array.IndexOf(uvLabelArray, SGUILayout.Constants.screenSpaceUVLabel);
				}

				public void SetWorldPositionUV()
				{
					UvSource = UvSourceType.WorldPosition;
					UvChannelsOptions = "XYZ";
					var uvLabelArray = program == ProgramType.Vertex ? SGUILayout.Constants.UvChannelOptionsVertex : SGUILayout.Constants.UvChannelOptions;
					UvChannel = Array.IndexOf(uvLabelArray, SGUILayout.Constants.worldPosUVLabel);
				}

				public void SetTriplanarUV()
				{
					UvSource = UvSourceType.Triplanar;
					var uvLabelArray = program == ProgramType.Vertex ? SGUILayout.Constants.UvChannelOptionsVertex : SGUILayout.Constants.UvChannelOptions;
					UvChannel = Array.IndexOf(uvLabelArray, SGUILayout.Constants.triplanarUVLabel);
				}

				public void SetShaderPropertyUV()
				{
					UvSource = UvSourceType.OtherShaderProperty;
					var uvLabelArray = program == ProgramType.Vertex ? SGUILayout.Constants.UvChannelOptionsVertex : SGUILayout.Constants.UvChannelOptions;
					UvChannel = Array.IndexOf(uvLabelArray, SGUILayout.Constants.shaderPropertyUVLabel);
				}

				public void SetCustomMaterialPropertyUV()
				{
					UvSource = UvSourceType.CustomMaterialProperty;
					var uvLabelArray = program == ProgramType.Vertex ? SGUILayout.Constants.UvChannelOptionsVertex : SGUILayout.Constants.UvChannelOptions;
					UvChannel = Array.IndexOf(uvLabelArray, SGUILayout.Constants.customMaterialPropertyUVLabel);
				}

				string GetUV(string input, string output, ProgramType programType)
				{
					if (UvSource == UvSourceType.Triplanar)
					{
						return "triplanar";
					}

					if (UvSource == UvSourceType.ScreenSpace)
					{
						return "screenUV";
					}
					else if(UvSource == UvSourceType.WorldPosition)
					{
						if (program == ProgramType.Vertex)
						{
							return string.Format("{0}.{1}", ShaderGenerator2.IsURP ? "worldPosUv" : "worldPosUv", UVChannels.ToLowerInvariant());
						}
						else
						{
							// assume Fragment
							return string.Format("{0}.{1}", ShaderGenerator2.IsURP ? "positionWS" : input + ".worldPos", UVChannels.ToLowerInvariant());
						}
					}
					else if (UvSource == UvSourceType.OtherShaderProperty)
					{
						if (LinkedShaderProperty.IsUsedInLightingFunction && ShaderGenerator2.CurrentPassHasLightingFunction)
							return string.Format("{0}.{1}.{2}", output, LinkedShaderProperty.GetVariableName(), UVChannels.ToLowerInvariant());
						else
							return string.Format("{0}.{1}", LinkedShaderProperty.GetVariableName(), UVChannels.ToLowerInvariant());
					}
					else if (UvSource == UvSourceType.CustomMaterialProperty)
					{
						string variable = this.ParentShaderProperty.Program == ProgramType.Vertex ? LinkedCustomMaterialProperty.PrintVariableVertex() : LinkedCustomMaterialProperty.PrintVariableFragment();
						return string.Format("{0}.{1}", variable, UVChannels.ToLowerInvariant());
					}
					else
					{
						string coord = ShaderGenerator2.VariablesManager.GetVariable("texcoord" + UvChannel);
						if (string.IsNullOrEmpty(coord))
						{
							if (programType == ProgramType.Vertex)
							{
								// no packed variable and in vertex program, so it must be a texcoord only used in the vertex function
								return string.Format("{0}.{1}.xy", input, "texcoord" + UvChannel);
							}
							else
							{
								Debug.LogError(ShaderGenerator2.ErrorMsg("Can't find UV coordinates for shader property: " + ParentShaderProperty.Name));
								return null;
							}
						}
						else
						{
							string result = string.Format("{0}.{1}.xy", programType == ProgramType.Vertex ? output : input, coord);
							result = result.Replace(".xy.xy", ".xy").Replace(".zw.xy", ".zw");
							return result;
						}
					}
				}

				#region Tiling/Offset & Scrolling Variables


				internal string GetDefaultTilingOffsetVariable()
				{
					return FetchVariable(GetTilingOffsetVariableName(), true);
				}

				string GetTilingOffsetVariableName()
				{
					return string.Format("{0}_ST", PropertyName);
				}

				// Uses a tiling/offset variable from another property
				bool UseCustomTilingOffsetVariable()
				{
					return !string.IsNullOrEmpty(TilingOffsetVariable);
				}

				// Returns true if this property's tiling/offset variable can be referenced
				bool HasValidTilingOffsetVariable()
				{
					return this.UseTilingOffset && !this.GlobalTilingOffset && !this.UseCustomTilingOffsetVariable();
				}


				internal string GetDefaultScrollingVariable()
				{
					return FetchVariable(GetScrollingVariableName(), true);
				}

				string GetScrollingVariableName()
				{
					return string.Format("{0}_SC", PropertyName);
				}

				// Uses a tiling/offset variable from another property
				bool UseCustomScrollingVariable()
				{
					return !string.IsNullOrEmpty(ScrollingVariable);
				}

				// Returns true if this property's tiling/offset variable can be referenced
				bool HasValidScrollingVariable()
				{
					return this.UseScrolling && !this.GlobalScrolling && !this.UseCustomScrollingVariable();
				}


				internal string GetDefaultOffsetSpeedVariable()
				{
					return FetchVariable(string.Format("{0}_OffsetSpeed", PropertyName), true);
				}

				internal string GetDefaultSineAnimVariable()
				{
					return FetchVariable(GetSineAnimVariableName(), true);
				}

				string GetSineAnimVariableName()
				{
					// x: speed, y: amplitude, z: frequency, w: unused
					return string.Format("{0}_SinAnimParams", PropertyName);
				}

				// Uses a UV sin anim variable from another property
				internal bool UseCustomSineAnimVariable()
				{
					return !string.IsNullOrEmpty(SineAnimationVariable);
				}

				// Returns true if this property's UV sin anim variable can be referenced
				bool HasValidSineAnimVariable()
				{
					return this.SineAnimation && !this.GlobalSineAnimation && !this.UseCustomSineAnimVariable();
				}

#if UNITY_2019_4_OR_NEWER
				string GetSamplerVariableName()
				{
					return string.Format("sampler{0}", this.PropertyName);
				}
				
				string GetSamplerVariableUsed()
				{
					if (!UseSeparateSampler)
					{
						return null;
					}
					
					// note: "sampler" prefix is omitted, because the macro automatically adds it
					return SeparateSamplerName;
				}
				
				bool HasValidSamplerVariable()
				{
					return !this.UseSeparateSampler && this.CanUseSeparateSampler;
				}

				void VerifySamplerValidity()
				{
					InvalidSampler = false;
					if (UseSeparateSampler)
					{
						var availableSamplers = FetchValidSamplerValues();
						if (!availableSamplers.Exists(val => val.value == this.SeparateSamplerName))
						{
							InvalidSampler = true;
						}
					}
				}
#else
				void VerifySamplerValidity()
				{
					
				}
#endif
				
				/// <summary>
				/// Verify that the tiling/offset & scrolling values are correct if they reference another implementation
				/// </summary>
				void VerifyReferencedValuesValidity()
				{
					invalidTilingOffsetVariable = false;
					if (UseTilingOffset && !string.IsNullOrEmpty(TilingOffsetVariable))
					{
						var availableValues = FetchValidTilingOffsetValues();
						if (!availableValues.Exists(av => av.valueLabel == TilingOffsetVariable && string.IsNullOrEmpty(av.disabled)))
						{
							invalidTilingOffsetVariable = true;
						}
					}

					invalidScrollingVariable = false;
					if (UseScrolling && !string.IsNullOrEmpty(ScrollingVariable))
					{
						var availableValues = FetchValidScrollingValues();
						if (!availableValues.Exists(av => av.valueLabel == ScrollingVariable && string.IsNullOrEmpty(av.disabled)))
						{
							invalidScrollingVariable = true;
						}
					}
					
					invalidSinAnimVariable = false;
					if (SineAnimation && !string.IsNullOrEmpty(SineAnimationVariable))
					{
						var availableValues = FetchValidSinAnimValues();
						if (!availableValues.Exists(av => av.valueLabel == SineAnimationVariable && string.IsNullOrEmpty(av.disabled)))
						{
							invalidSinAnimVariable = true;
						}
					}
				}

				struct AvailableValue
				{
					public string value;
					public string label;
					public string valueLabel;
					public string disabled;

					public override string ToString()
					{
						return string.Format("[AvailableValue value: {0}, label: {1}, valueLabel: {2}, disabled: {3}]", value, label, valueLabel, disabled);
					}
				}

				/// <summary>
				/// Returns the currently available tiling/offset values
				/// </summary>
				List<AvailableValue> FetchValidTilingOffsetValues()
				{
					return FetchValidValuesGeneric(imp => imp.HasValidTilingOffsetVariable(), imp => imp.GetDefaultTilingOffsetVariable(), imp => imp.GetTilingOffsetVariableName());
				}

				/// <summary>
				/// Returns the currently available tiling/offset values
				/// </summary>
				List<AvailableValue> FetchValidScrollingValues()
				{
					return FetchValidValuesGeneric(imp => imp.HasValidScrollingVariable(), imp => imp.GetDefaultScrollingVariable(), imp => imp.GetScrollingVariableName());
				}

				/// <summary>
				/// Returns the currently available UV sin anim values
				/// </summary>
				List<AvailableValue> FetchValidSinAnimValues()
				{
					return FetchValidValuesGeneric(imp => imp.HasValidSineAnimVariable(), imp => imp.GetDefaultSineAnimVariable(), imp => imp.GetSineAnimVariableName());
				}

#if UNITY_2019_4_OR_NEWER
				/// <summary>
				/// Returns the currently available texture sampler values
				/// </summary>
				List<AvailableValue> FetchValidSamplerValues()
				{
					return FetchValidValuesGeneric(
						imp => this.SamplerGroup == imp.SamplerGroup && imp.HasValidSamplerVariable() && imp.ParentShaderProperty.passBitmask == this.ParentShaderProperty.passBitmask,
						imp => { return imp.PropertyName; }, 
						imp => imp.GetSamplerVariableName());
				}
#endif

				// Generic function to return available tiling/offset or scrolling variables
				List<AvailableValue> FetchValidValuesGeneric(Func<Imp_MaterialProperty_Texture, bool> checkFunction, Func<Imp_MaterialProperty_Texture, string> valueFunction, Func<Imp_MaterialProperty_Texture, string> valueLabelFunction)
				{
					var list = new List<AvailableValue>();

					if (ShaderGenerator2.CurrentConfig == null || ShaderGenerator2.CurrentConfig.VisibleShaderProperties == null)
					{
						return list;
					}

					foreach (var sp in ShaderGenerator2.CurrentConfig.VisibleShaderProperties)
					{
						foreach (var imp in sp.implementations)
						{
							if (imp == this)
							{
								continue;
							}

							// Check regular texture implementations
							var imp_mp_text = imp as Imp_MaterialProperty_Texture;
							if (imp_mp_text != null)
							{
								if (checkFunction(imp_mp_text))
								{
									list.Add(new AvailableValue()
									{
										value = valueFunction(imp_mp_text),
										label = imp_mp_text.Label,
										valueLabel = valueLabelFunction(imp_mp_text),
										disabled = null
									});
								}
							}
						}
					}

					// Check Custom Material Properties with texture implementation
					foreach (var cmp in ShaderGenerator2.CurrentConfig.CustomMaterialProperties)
					{
						var imp_mp_ct = cmp.implementation as Imp_MaterialProperty_Texture;
						if (imp_mp_ct != null)
						{
							if (checkFunction(imp_mp_ct))
							{
								list.Add(new AvailableValue()
								{
									value = valueFunction(imp_mp_ct),
									label = imp_mp_ct.Label,
									valueLabel = valueLabelFunction(imp_mp_ct),
									disabled = imp_mp_ct.IsCustomMaterialPropertyReferenced() ? null : "(unused Custom Material Property)"
								});
							}
						}
					}

					return list;
				}

				#endregion

				internal override string PrintPropertyInternal(string indent)
				{
					bool noScaleOffset = !(UseTilingOffset && !UseCustomTilingOffsetVariable());
					noScaleOffset |= ParentShaderProperty.layerCloneSuffix != null && this.GlobalTilingOffset;
					
					var prop = base.PrintPropertyInternal(indent) + string.Format("{3}{0} (\"{1}\", 2D) = \"{2}\" {{}}", PropertyName, Label, DefaultValue, noScaleOffset ? "[NoScaleOffset] " : "");
					if (UseScrolling && !UseCustomScrollingVariable())
						prop += string.Format("\n{0}[TCP2UVScrolling] {1}_SC (\"{2} UV Scrolling\", Vector) = (1,1,0,0)", indent, PropertyName, Label);
					if (RandomOffset)
						prop += string.Format("\n{0}{1} (\"{2} UV Offset Speed\", Float) = 120", indent, GetDefaultOffsetSpeedVariable(), Label);
					if (SineAnimation && !UseCustomSineAnimVariable())
						prop += string.Format("\n{0}[TCP2Vector4FloatsDrawer(Speed,Amplitude,Frequency,Offset)] {1} (\"{2} UV Sine Distortion Parameters\", Float) = (1, 0.05, 1, 0)", indent, GetDefaultSineAnimVariable(), Label);
					if (MipProperty)
						prop += string.Format("\n{0}{1}_Mip (\"{2} Mip Level\", Range(0,10)) = 0", indent, PropertyName, Label);
					return prop;
				}
				internal override string PrintVariableDeclareOutsideCBuffer(string indent)
				{
#if UNITY_2019_4_OR_NEWER
					if (UseOldSampler2DSyntax)
					{
						return string.Format("{0}sampler2D {1};", indent, PropertyName);
					}
					return string.Format(UseSeparateSampler ? "{0}TCP2_TEX2D_NO_SAMPLER({1});" : "{0}TCP2_TEX2D_WITH_SAMPLER({1});", indent, PropertyName);
#else
					return string.Format("{0}sampler2D {1};", indent, PropertyName);
#endif
				}
				internal override string PrintVariableDeclare(string indent)
				{
					string properties = "";
					if (UseTilingOffset && !UseCustomTilingOffsetVariable())
						properties += string.Format("{0}float4 {1}_ST;\n", indent, PropertyName);
					if (ScaleByTexelSize)
						properties += string.Format("{0}float4 {1}_TexelSize;\n", indent, PropertyName);
					if (UseScrolling && !UseCustomScrollingVariable())
						properties += string.Format("{0}half4 {1}_SC;\n", indent, PropertyName);
					if (RandomOffset)
						properties += string.Format("{0}half {1};\n", indent, GetDefaultOffsetSpeedVariable());
					if (SineAnimation && !UseCustomSineAnimVariable())
						properties += string.Format("{0}half4 {1};\n", indent, GetDefaultSineAnimVariable());
					if (MipProperty)
						properties += string.Format("{0}fixed {1}_Mip;\n", indent, PropertyName);
					properties = properties.TrimEnd('\n');
					return string.IsNullOrEmpty(properties) ? null : properties;
				}

				protected override string PrintVariableInternal(string indent)
				{
					return null;
				}

				internal override string PrintVariableFragment(string inputSource, string outputSource, string arguments)
				{
					var tilingOffsetVariable = UseCustomTilingOffsetVariable() ? TilingOffsetVariable : GetDefaultTilingOffsetVariable();
					var tilingMod = ScaleByTexelSize ? string.Format(" * {0}_TexelSize.xy", PropertyName) : "";
					if (UvSource == UvSourceType.ScreenSpace)
					{
						tilingMod += ScaleByTexelSize ? " * _ScreenParams.xy" : " * _ScreenParams.zw";
					}
					tilingMod += (UseTilingOffset && (!GlobalTilingOffset || UvSource != UvSourceType.Texcoord)) ? string.Format(" * {0}.xy", tilingOffsetVariable) : "";
					var offsetMod = (UseTilingOffset && (!GlobalTilingOffset || UvSource != UvSourceType.Texcoord)) ? string.Format(" + {0}.zw", tilingOffsetVariable) : "";
					var scrollingVariable = UseCustomScrollingVariable() ? ScrollingVariable : GetDefaultScrollingVariable();
					var scrollingMod = (UseScrolling && !GlobalScrolling) ? string.Format(" + {1}(_Time.yy * {0}.xy)", scrollingVariable, NoTile ? "" : "frac") : "";
					var randomOffsetMod = (RandomOffset && !GlobalRandomOffset) ? string.Format(" + hash22(floor(_Time.xx * {0}.xx) / {0}.xx)", GetDefaultOffsetSpeedVariable()) : "";

					string uvSineMod;
					if (SineAnimation && !GlobalSineAnimation)
					{
						string uvSinProperty = UseCustomSineAnimVariable() ? SineAnimationVariable : GetDefaultSineAnimVariable();
						string uvSinVariable = string.Format("uvSinAnim_{0}", PropertyName);
						string uvSinPos = UvSource == UvSourceType.WorldPosition ? "sinUvAnimVertexWorldPos" : "sinUvAnimVertexPos";
						string uvSinInput = string.Format("{0}.{1}", inputSource, ShaderGenerator2.IsURP ? "[[INPUT_VALUE:" + uvSinPos + "]]" : uvSinPos);
						string uvSinCalculation = string.Format("float2 {0} = ({1} * {2}.z) + (_Time.yy * {2}.x);", uvSinVariable, uvSinInput, uvSinProperty);
						ShaderGenerator2.AppendLineBefore(uvSinCalculation);
						uvSineMod = string.Format(" + (((sin(0.9 * {0} + {1}.w) + sin(1.33 * {0} + 3.14 * {1}.w) + sin(2.4 * {0} + 5.3 * {1}.w)) / 3) * {1}.y)", uvSinVariable, uvSinProperty);
					}
					else
					{
						uvSineMod = "";
					}

					// uv coordinates
					string coords = null;
					if (!string.IsNullOrEmpty(arguments))
					{
						var uv = TryGetArgument("uv", arguments);
						if (uv != null)
						{
							coords = uv;
						}
					}
					if (coords == null)
					{
						coords = GetUV(inputSource, outputSource, ProgramType.Fragment);
					}

					// function
#if UNITY_2019_4_OR_NEWER
					string function;
					if (!UseOldSampler2DSyntax)
						function = NoTile ? "TCP2_TEX2D_SAMPLE_NOTILE" : "TCP2_TEX2D_SAMPLE";
					else
#endif
						function = NoTile ? "tex2D_noTile" : "tex2D";

					// channels
					var hideChannels = TryGetArgument("hide_channels", arguments);
					var channels = string.IsNullOrEmpty(hideChannels) ? "." + Channels.ToLowerInvariant() : "";
					
#if UNITY_2019_4_OR_NEWER
					// sampler
					string sampler = GetSamplerVariableUsed();
					if (sampler == null)
					{
						sampler = PropertyName;
					}
#endif
					
					if (UvSource == UvSourceType.Triplanar)
					{
#if UNITY_2019_4_OR_NEWER
						if (!UseOldSampler2DSyntax)
							function = NoTile ? "TCP2_TEX2D_SAMPLE_TRIPLANAR_NOTILE" : "TCP2_TEX2D_SAMPLE_TRIPLANAR";
						else
#endif
							function = NoTile ? "tex2D_triplanar_noTile" : "tex2D_triplanar";

						bool useTilingOffset = UseTilingOffset && (!GlobalTilingOffset || UvSource != UvSourceType.Texcoord);
						string texelScaling = ScaleByTexelSize ? string.Format(" * {0}_TexelSize.xy", PropertyName) : "";
						string triplanarTiling = string.Format(CultureInfo.InvariantCulture, "float2({0}, {0})", UVTriplanarScale);
						if (useTilingOffset)
						{
							triplanarTiling += string.Format(" * {0}.xy", tilingOffsetVariable);
						}
						string triplanarOffset = useTilingOffset ? tilingOffsetVariable + ".zw" : "float2(0,0)";

						string triplanarTilingOffset;
						if (scrollingMod != "" || randomOffsetMod != "" || texelScaling != "" || uvSineMod != "")
						{
							triplanarTilingOffset = string.Format("float4({0}{5}, {1}{2}{3}{4})", triplanarTiling, triplanarOffset, scrollingMod, randomOffsetMod, uvSineMod, texelScaling);
						}
						else
						{
							triplanarTilingOffset = useTilingOffset ?
								string.Format(CultureInfo.InvariantCulture, "float4({0}, {0}, 1, 1) * {1}", UVTriplanarScale, tilingOffsetVariable) :
								string.Format(CultureInfo.InvariantCulture, "float4({0}, {0}, 0, 0)", UVTriplanarScale);
						}

						// figure out position/normal input values
						string worldPositionInput;
						string worldNormalInput;
						if (LocalSpaceTriplanar)
						{
							worldPositionInput = inputSource + ".[[INPUT_VALUE:objPos]]";
							worldNormalInput = inputSource + ".[[INPUT_VALUE:objNormal]]";
						}
						else
						{
							worldPositionInput = ShaderGenerator2.IsURP ? "positionWS" : inputSource + ".[[INPUT_VALUE:worldPos]]";
							worldNormalInput = ShaderGenerator2.IsURP ? "normalWS" : inputSource + ".[[INPUT_VALUE:worldNormal]]";
						}
						
#if UNITY_2019_4_OR_NEWER
						if (!UseOldSampler2DSyntax)
							return string.Format("{0}({1}, {2}, {3}, {4}, {5}){6}", function, PropertyName, sampler, triplanarTilingOffset, worldPositionInput, worldNormalInput, channels);
						else
#endif
						return string.Format("{0}({1}, {2}, {3}, {4})", function, PropertyName, triplanarTilingOffset, worldPositionInput, worldNormalInput);
					}

#if UNITY_2019_4_OR_NEWER
					if (!UseOldSampler2DSyntax)
						return string.Format("{0}({1}, {2}, {3}{4}{5}{6}{7}{8}){9}", function, PropertyName, sampler, coords, tilingMod, scrollingMod, offsetMod, randomOffsetMod, uvSineMod, channels);
					else
#endif
						return string.Format("{0}({1}, {2}{3}{4}{5}{6}{7}){8}", function, PropertyName, coords, tilingMod, scrollingMod, offsetMod, randomOffsetMod, uvSineMod, channels);
				}
				
				internal override string PrintVariableVertex(string inputSource, string outputSource, string arguments)
				{
					var tilingOffsetVariable = UseCustomTilingOffsetVariable() ? TilingOffsetVariable : GetDefaultTilingOffsetVariable();
					var tilingMod = ScaleByTexelSize ? string.Format(" * {0}_TexelSize.xy", PropertyName) : "";
					if (UvSource == UvSourceType.ScreenSpace)
					{
						tilingMod += ScaleByTexelSize ? " * _ScreenParams.xy" : " * _ScreenParams.zw";
					}
					tilingMod += (UseTilingOffset && (!GlobalTilingOffset || UvSource != UvSourceType.Texcoord)) ? string.Format(" * {0}.xy", tilingOffsetVariable) : "";
					var offsetMod = (UseTilingOffset && (!GlobalTilingOffset || UvSource != UvSourceType.Texcoord)) ? string.Format(" + {0}.zw", tilingOffsetVariable) : "";
					var scrollingVariable = UseCustomScrollingVariable() ? ScrollingVariable : GetDefaultScrollingVariable();
					var scrollingMod = (UseScrolling && !GlobalScrolling) ? string.Format(" + {1}(_Time.yy * {0}.xy)", scrollingVariable, NoTile ? "" : "frac") : "";
					var randomOffsetMod = (RandomOffset && !GlobalRandomOffset) ? string.Format(" + hash22(floor(_Time.xx * {0}.xx) / {0}.xx)", GetDefaultOffsetSpeedVariable()) : "";

					string uvSineMod;
					if (SineAnimation)
					{
						string uvSinProperty = UseCustomSineAnimVariable() ? SineAnimationVariable : GetDefaultSineAnimVariable();
						string uvSinVariable = string.Format("uvSinAnim_{0}", PropertyName);
						string uvSinInput = UvSource == UvSourceType.WorldPosition ? "sinUvAnimVertexWorldPos" : "sinUvAnimVertexPos";
						string uvSinCalculation = string.Format("float2 {0} = ({1} * {2}.z) + (_Time.yy * {2}.x);", uvSinVariable, uvSinInput, uvSinProperty);
						ShaderGenerator2.AppendLineBefore(uvSinCalculation);
						uvSineMod = string.Format(" + (((sin(0.9 * {0} + {1}.w) + sin(1.33 * {0} + 3.14 * {1}.w) + sin(2.4 * {0} + 5.3 * {1}.w)) / 3) * {1}.y)", uvSinVariable, uvSinProperty);
					}
					else
					{
						uvSineMod = "";
					}


					// uv coordinates
					string coords = null;
					if (!string.IsNullOrEmpty(arguments))
					{
						var uv = TryGetArgument("uv", arguments);
						if (uv != null)
						{
							coords = uv;
						}
					}
					if (coords == null)
					{
						coords = GetUV(inputSource, outputSource, ProgramType.Vertex);
					}
					var hideChannels = TryGetArgument("hide_channels", arguments);
					var channels = string.IsNullOrEmpty(hideChannels) ? "." + Channels.ToLowerInvariant() : "";

#if UNITY_2019_4_OR_NEWER
					// sampler
					string sampler = GetSamplerVariableUsed();
					if (sampler == null)
					{
						sampler = PropertyName;
					}

					string function = NoTile ? "TCP2_TEX2D_SAMPLE_LOD_NOTILE" : "TCP2_TEX2D_SAMPLE_LOD";
#else
					string function = NoTile ? "tex2Dlod_noTile" : "tex2Dlod";
#endif
					if (UvSource == UvSourceType.Triplanar)
					{
#if UNITY_2019_4_OR_NEWER
						if (!UseOldSampler2DSyntax)
							function = NoTile ? "TCP2_TEX2D_SAMPLE_LOD_TRIPLANAR_NOTILE" : "TCP2_TEX2D_SAMPLE_LOD_TRIPLANAR";
						else
#endif
						function = NoTile ? "tex2Dlod_triplanar_noTile" : "tex2Dlod_triplanar";

						bool useTilingOffset = UseTilingOffset && !GlobalTilingOffset;
						string triplanarTiling = string.Format(CultureInfo.InvariantCulture, "float2({0}, {0})", UVTriplanarScale);
						if (useTilingOffset)
						{
							triplanarTiling += string.Format(" * {0}.xy", tilingOffsetVariable);
						}
						string triplanarOffset = useTilingOffset ? tilingOffsetVariable + ".zw" : "float2(0,0)";

						string triplanarTilingOffset;
						if (scrollingMod != "" || randomOffsetMod != "" || uvSineMod != "")
						{
							triplanarTilingOffset = string.Format("float4({0}, {1}{2}{3}{4})", triplanarTiling, triplanarOffset, scrollingMod, randomOffsetMod, uvSineMod);
						}
						else
						{
							triplanarTilingOffset = useTilingOffset ?
								string.Format(CultureInfo.InvariantCulture, "float4({0}, {0}, 1, 1) * {1}", UVTriplanarScale, tilingOffsetVariable) :
								string.Format(CultureInfo.InvariantCulture, "float4({0}, {0}, 0, 0)", UVTriplanarScale);							
						}

						string worldPositionInput = LocalSpaceTriplanar ? "v.vertex.xyz" : "worldPosUv";
						string worldNormalInput = LocalSpaceTriplanar ? "v.normal.xyz" : "worldNormalUv";

#if UNITY_2019_4_OR_NEWER
						if (!UseOldSampler2DSyntax)
							return string.Format("{0}({1}, {2}, {3}, {4}, {5}, {6})", function, PropertyName, sampler, triplanarTilingOffset, GetMipValue(), worldPositionInput, worldNormalInput);
						else
#endif
						return string.Format("{0}({1}, {2}, {3}, {4}, {5})", function, PropertyName, triplanarTilingOffset, GetMipValue(), worldPositionInput, worldNormalInput);
					}

#if UNITY_2019_4_OR_NEWER
					return string.Format("{0}({1}, {2}, {3}{4}{5}{6}{7}{8}, {9}){10}", function, PropertyName, sampler, coords, tilingMod, scrollingMod, offsetMod, randomOffsetMod, uvSineMod, GetMipValue(), channels);
#else
					return string.Format("tex2Dlod({0}, float4({1}{2}{3}{4}{5}{6}, 0, {7})){8}", PropertyName, coords, tilingMod, scrollingMod, offsetMod, randomOffsetMod, uvSineMod, GetMipValue(), channels);
#endif
				}

				internal override void NewLineGUI(bool usedByCustomCode)
				{
					base.NewLineGUI(usedByCustomCode);

					BeginHorizontal();
					{
						var index = Array.IndexOf(SGUILayout.Constants.DefaultTextureValues, DefaultValue);
						var newIndex = index;
						if (newIndex < 0) newIndex = 0;

						bool highlighted = !IsDefaultImplementation ? DefaultValue != SGUILayout.Constants.DefaultTextureValues[0] : DefaultValue != GetDefaultImplementation<Imp_MaterialProperty_Texture>().DefaultValue;
						SGUILayout.InlineLabel("Default Value", highlighted);
						newIndex = SGUILayout.Popup(newIndex, SGUILayout.Constants.DefaultTextureValues);

						if (newIndex != index)
							DefaultValue = SGUILayout.Constants.DefaultTextureValues[newIndex];
					}
					EndHorizontal();

					if (!IsCustomMaterialProperty)
					{
						BeginHorizontal();
						{
							bool highlighted = !IsDefaultImplementation ? Channels != DefaultChannels : Channels != GetDefaultImplementation<Imp_MaterialProperty_Texture>().Channels;
							SGUILayout.InlineLabel("Swizzle", highlighted);

							if (usedByCustomCode)
							{
								using (new EditorGUI.DisabledScope(true))
								{
									GUILayout.Label(TCP2_GUI.TempContent("Defined in Custom Code"), SGUILayout.Styles.ShurikenValue, GUILayout.Height(16), GUILayout.ExpandWidth(false));
								}
							}
							else
							{
								if (ChannelsCount == 1)
								{
									Channels = SGUILayout.RGBASelector(Channels);
								}
								else
								{
									Channels = SGUILayout.RGBASwizzle(Channels, ChannelsCount);
								}
							}
						}
						EndHorizontal();
						
#if UNITY_2019_4_OR_NEWER
						BeginHorizontal();
						{
							using (new EditorGUI.DisabledScope(!CanUseSeparateSampler))
							{
								bool highlighted = !IsDefaultImplementation ? SeparateSamplerName != null : SeparateSamplerName != GetDefaultImplementation<Imp_MaterialProperty_Texture>().SeparateSamplerName;
								SGUILayout.InlineLabel("Sampler", highlighted);

								string label = UseSeparateSampler ? SeparateSamplerName : "Default";
								string tooltip = CanUseSeparateSampler ? null : "Using a separate sampler isn't compatible with 'No Tile' and 'Triplanar' UVs with the Built-in Render Pipeline due to bugs in its Surface Shader system";
								if (SGUILayout.ButtonPopup(TCP2_GUI.TempContent(label, tooltip)))
								{
									GenericMenu.MenuFunction2 OnSelectSampler = (sampler) =>
									{
										SeparateSamplerName = (sampler == null) ? null : (string)sampler;
									};

									GenericMenu menu = new GenericMenu();
									menu.AddItem(new GUIContent("Default"), !UseSeparateSampler, OnSelectSampler, null);

									var samplerList = FetchValidSamplerValues();
									if (samplerList.Count > 0)
									{
										menu.AddSeparator("");
										foreach (var availableValue in samplerList)
										{
											string itemLabel = string.Format("{0}: {1}", availableValue.label, availableValue.valueLabel); // note: has non-breaking space character
											menu.AddItem(new GUIContent(itemLabel), SeparateSamplerName == availableValue.value, OnSelectSampler, availableValue.value);
										}
									}

									menu.ShowAsContext();
								}
							}
						}
						EndHorizontal();
#endif
					}

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? UvChannel > 0 : UvChannel != GetDefaultImplementation<Imp_MaterialProperty_Texture>().UvChannel;
						bool uvExpanded = SGUILayout.Foldout(uvExpandedCache, "UV", highlighted);
						if (uvExpanded != uvExpandedCache)
						{
							if (uvExpanded)
							{
								ParentShaderProperty.implementationsExpandedStates.Add(this.guid.GetHashCode());
							}
							else
							{
								ParentShaderProperty.implementationsExpandedStates.Remove(this.guid.GetHashCode());
							}
							_uvExpandedCache = null;
						}

						if (IsUvLocked)
						{
							using (new EditorGUI.DisabledScope(true))
							{
								SGUILayout.Popup(0, SGUILayout.Constants.LockedUvChannelOptions);
							}
						}
						else
						{
							var uvLabelArray = program == ProgramType.Vertex ? SGUILayout.Constants.UvChannelOptionsVertex : SGUILayout.Constants.UvChannelOptions;
							UvChannel = SGUILayout.Popup(UvChannel, uvLabelArray);
							if (GUI.changed)
							{
								if (Array.IndexOf(uvLabelArray, SGUILayout.Constants.screenSpaceUVLabel) == UvChannel && !IsUvLocked)
								{
									UvSource = UvSourceType.ScreenSpace;
								}
								else if (Array.IndexOf(uvLabelArray, SGUILayout.Constants.worldPosUVLabel) == UvChannel && !IsUvLocked)
								{
									UvSource = UvSourceType.WorldPosition;
									UvChannelsOptions = "XYZ";
								}
								else if (Array.IndexOf(uvLabelArray, SGUILayout.Constants.shaderPropertyUVLabel) == UvChannel && !IsUvLocked)
								{
									UvSource = UvSourceType.OtherShaderProperty;
									UvChannelsOptions = "XXX";
								}
								else if (Array.IndexOf(uvLabelArray, SGUILayout.Constants.customMaterialPropertyUVLabel) == UvChannel && !IsUvLocked)
								{
									UvSource = UvSourceType.CustomMaterialProperty;
									UvChannelsOptions = "XXX";
								}
								else if (Array.IndexOf(uvLabelArray, SGUILayout.Constants.triplanarUVLabel) == UvChannel && !IsUvLocked)
								{
									UvSource = UvSourceType.Triplanar;
									UvChannelsOptions = "XXX";
								}
								else
								{
									UvSource = UvSourceType.Texcoord;
								}
							}
						}

						if (UvSource == UvSourceType.WorldPosition || UvSource == UvSourceType.OtherShaderProperty || UvSource == UvSourceType.CustomMaterialProperty)
						{
							var gc = TCP2_GUI.TempContent(".");
							var rect = GUILayoutUtility.GetRect(gc, SGUILayout.Styles.GrayMiniLabel, GUILayout.ExpandWidth(false));
#if !UNITY_2019_3_OR_NEWER
							rect.y -= 2;
#endif
							GUI.Label(rect, gc, SGUILayout.Styles.GrayMiniLabel);
							UVChannels = SGUILayout.GenericSwizzle(UVChannels, 2, UvChannelsOptions, 30, showAvailableChannels: false);
						}

						if (UvSource == UvSourceType.Triplanar)
						{
							var gc = TCP2_GUI.TempContent("Scale:");
							var rect = GUILayoutUtility.GetRect(gc, SGUILayout.Styles.GrayMiniLabel, GUILayout.ExpandWidth(false));
#if !UNITY_2019_3_OR_NEWER
							rect.y -= 2;
#endif
							GUI.Label(rect, gc, SGUILayout.Styles.GrayMiniLabel);
							UVTriplanarScale = SGUILayout.FloatField(UVTriplanarScale);
						}
					}
					EndHorizontal();

					if (uvExpandedCache)
					{
						//SGUILayout.Indent += 10;

						bool showScreenSpaceUVOptions = UvSource == UvSourceType.ScreenSpace;
						if (GlobalOptions.data.ShowDisabledFeatures || showScreenSpaceUVOptions)
						{
							using (new EditorGUI.DisabledGroupScope(!showScreenSpaceUVOptions))
							{
								BeginHorizontal();
								{
									bool highlighted = !IsDefaultImplementation ? ScreenSpaceUVVertex : ScreenSpaceUVVertex != GetDefaultImplementation<Imp_MaterialProperty_Texture>().ScreenSpaceUVVertex;
									SGUILayout.InlineLabel("└   Vertex SSUV", "Calculate the screen space UV in the vertex shader, faster but can appear distorted", highlighted);
									ScreenSpaceUVVertex = SGUILayout.Toggle(ScreenSpaceUVVertex);
								}
								EndHorizontal();

								using (new EditorGUI.DisabledGroupScope(ScreenSpaceUVVertex))
								{
									BeginHorizontal();
									{
										bool highlighted = !IsDefaultImplementation ? ScreenSpaceUVObjectOffset : ScreenSpaceUVObjectOffset != GetDefaultImplementation<Imp_MaterialProperty_Texture>().ScreenSpaceUVObjectOffset;
										SGUILayout.InlineLabel("└   Obj Offset SSUV", "Align the UV with the object's pivot, so that the texture doesn't appear fixed on the screen (remove the 'shower door' effect)", highlighted);
										ScreenSpaceUVObjectOffset = SGUILayout.Toggle(ScreenSpaceUVObjectOffset);
									}
									EndHorizontal();
								}
							}
						}

						bool showTriplanarUvOptions = UvSource == UvSourceType.Triplanar;
						if (GlobalOptions.data.ShowDisabledFeatures || showTriplanarUvOptions)
						{
							using (new EditorGUI.DisabledGroupScope(!showTriplanarUvOptions))
							{
								BeginHorizontal();
								{
									bool highlighted = !IsDefaultImplementation ? LocalSpaceTriplanar : LocalSpaceTriplanar != GetDefaultImplementation<Imp_MaterialProperty_Texture>().LocalSpaceTriplanar;
									SGUILayout.InlineLabel("└   Object Space", "Calculate the Triplanar UV in object space instead of world space", highlighted);
									LocalSpaceTriplanar = SGUILayout.Toggle(LocalSpaceTriplanar);
								}
								EndHorizontal();
							}
						}


						BeginHorizontal();
						{
							bool highlighted = !IsDefaultImplementation ? UseTilingOffset : UseTilingOffset != GetDefaultImplementation<Imp_MaterialProperty_Texture>().UseTilingOffset;
							SGUILayout.InlineLabel("Tiling/Offset", highlighted);
							UseTilingOffset = SGUILayout.Toggle(UseTilingOffset);
						}
						EndHorizontal();

						bool showTilingOptions = UseTilingOffset && UvSource == UvSourceType.Texcoord;
						if ((GlobalOptions.data.ShowDisabledFeatures || showTilingOptions) && !IsUvLocked)
						{
							using (new EditorGUI.DisabledGroupScope(!showTilingOptions))
							{
								BeginHorizontal();
								{
									bool highlighted = !IsDefaultImplementation ? GlobalTilingOffset : GlobalTilingOffset != GetDefaultImplementation<Imp_MaterialProperty_Texture>().GlobalTilingOffset;
									SGUILayout.InlineLabel("└   Global", "Makes the tiling/offset values global to the selected UV coordinates: all textures using the same UV coordinates will inherit the tiling/offset values defined for this texture.\nIt also means that they will be calculated in the vertex shader (faster but uses one interpolator).\n\nDoes not apply to screen space UV coordinates.", highlighted);
									GlobalTilingOffset = SGUILayout.Toggle(GlobalTilingOffset);
								}
								EndHorizontal();
							}
						}

						showTilingOptions = UseTilingOffset && !(UvSource == UvSourceType.Texcoord && GlobalTilingOffset);
						if ((GlobalOptions.data.ShowDisabledFeatures || showTilingOptions))
						{
							using (new EditorGUI.DisabledGroupScope(!showTilingOptions))
							{
								BeginHorizontal();
								{
									bool highlighted = !IsDefaultImplementation ? UseCustomTilingOffsetVariable() : TilingOffsetVariable != GetDefaultImplementation<Imp_MaterialProperty_Texture>().TilingOffsetVariable;
									SGUILayout.InlineLabel("└   Variable", "Defines the tiling/offset uniform variable.\nBy default, a new property will be created for this texture, however you can use another texture's tiling/offset variable so that this texture will be linked with it. You would typically do that if you have a normal map coupled with an albedo map, for example.", highlighted);
									var tilingOffsetVar = UseCustomTilingOffsetVariable() ? TilingOffsetVariableLabel : GetTilingOffsetVariableName();
									if (SGUILayout.ButtonPopup(tilingOffsetVar))
									{
										var menu = new GenericMenu();
										string label = string.Format("{0}: {1}", ParentShaderProperty.Name, GetTilingOffsetVariableName()); // note: has non-breaking space character
										if (ParentShaderProperty.Name == "_CustomMaterialPropertyDummy") // TODO get rid of the dummy shader property for custom material properties?
										{
											label = GetTilingOffsetVariableName();
										}

										menu.AddItem(new GUIContent(label), !UseCustomTilingOffsetVariable(), () =>
										{
											TilingOffsetVariable = "";
											TilingOffsetVariableLabel = "";
											invalidTilingOffsetVariable = false;
										});

										// fetch available tiling/offset values and add them to the menu
										var itemList = new List<MenuItem>();
										var availableValues = FetchValidTilingOffsetValues();
										foreach(var availableValue in availableValues)
										{
											if (availableValue.label == this.Label)
											{
												continue;
											}

											if (string.IsNullOrEmpty(availableValue.disabled))
											{
												itemList.Add(new MenuItem()
												{
													guiContent = new GUIContent(string.Format("{0}: {1}", availableValue.label, availableValue.valueLabel)), // note: has non-breaking space character
													on = this.TilingOffsetVariable == availableValue.value,
													menuFunction = () =>
													{
														TilingOffsetVariable = availableValue.value;
														TilingOffsetVariableLabel = availableValue.valueLabel;
														invalidTilingOffsetVariable = false;
													}
												});
											}
											else
											{
												itemList.Add(new MenuItem()
												{
													guiContent = new GUIContent(string.Format("{0}: {1} {2}", availableValue.label, availableValue.valueLabel, availableValue.disabled)), // note: has non-breaking space character
													on = this.TilingOffsetVariable == availableValue.value,
													disabled = true
												});
											}
										}

										if (itemList.Count > 0)
										{
											menu.AddSeparator("");
											foreach (var item in itemList)
											{
												if (item.disabled)
												{
													menu.AddDisabledItem(item.guiContent);
												}
												else
												{
													menu.AddItem(item.guiContent, item.on, item.menuFunction);
												}
											}
										}

										menu.ShowAsContext();
									}
								}
								EndHorizontal();

								BeginHorizontal();
								{
									bool highlighted = !IsDefaultImplementation ? ScaleByTexelSize : ScaleByTexelSize != GetDefaultImplementation<Imp_MaterialProperty_Texture>().ScaleByTexelSize;
									SGUILayout.InlineLabel("└   Scale by Texel Size", "Will scale the UV by the texture's texel size. Usually useful to get pixel-perfect screen space UV mapping on the screen.", highlighted);
									ScaleByTexelSize = SGUILayout.Toggle(ScaleByTexelSize);
								}
								EndHorizontal();
							}
						}

						BeginHorizontal();
						{
							bool highlighted = !IsDefaultImplementation ? UseScrolling || RandomOffset || SineAnimation : (UseScrolling != GetDefaultImplementation<Imp_MaterialProperty_Texture>().UseScrolling || RandomOffset != GetDefaultImplementation<Imp_MaterialProperty_Texture>().RandomOffset || SineAnimation != GetDefaultImplementation<Imp_MaterialProperty_Texture>().SineAnimation);
							SGUILayout.InlineLabel("UV Animation", highlighted);
							int choice = UseScrolling ? 1 : (RandomOffset ? 2 : (SineAnimation ? 3 : 0));
							int new_choice = SGUILayout.Popup(choice, SGUILayout.Constants.UvAnimationOptions);
							if (new_choice != choice)
							{
								UseScrolling = false;
								RandomOffset = false;
								SineAnimation = false;

								switch (new_choice)
								{
									case 1: UseScrolling = true; break;
									case 2: RandomOffset = true; break;
									case 3: SineAnimation = true; break;
								}
							}
						}
						EndHorizontal();

						bool showUvAnimationOptions = (UseScrolling || RandomOffset || SineAnimation) && UvSource == UvSourceType.Texcoord;
						if ((GlobalOptions.data.ShowDisabledFeatures || showUvAnimationOptions) && !IsUvLocked)
						{
							using (new EditorGUI.DisabledGroupScope(!showUvAnimationOptions))
							{
								if (UseScrolling)
								{
									BeginHorizontal();
									{
										bool highlighted = !IsDefaultImplementation ? GlobalScrolling : GlobalScrolling != GetDefaultImplementation<Imp_MaterialProperty_Texture>().GlobalScrolling;
										SGUILayout.InlineLabel("└   Global", "Makes the scrolling global to the selected UV coordinates: all textures using the same UV coordinates will inherit the scrolling animation and values defined for this texture.\nIt also means that they will be calculated in the vertex shader (faster but uses one interpolator).", highlighted);
										GlobalScrolling = SGUILayout.Toggle(GlobalScrolling);
										GlobalRandomOffset = GlobalSineAnimation = GlobalScrolling;
									}
									EndHorizontal();

									bool showScrollingVariable = UseScrolling && !(UvSource == UvSourceType.Texcoord && GlobalScrolling);
									if ((GlobalOptions.data.ShowDisabledFeatures || showScrollingVariable))
									{
										using (new EditorGUI.DisabledGroupScope(!showScrollingVariable))
										{
											BeginHorizontal();
											{
												bool highlighted = !IsDefaultImplementation ? UseCustomScrollingVariable() : ScrollingVariable != GetDefaultImplementation<Imp_MaterialProperty_Texture>().ScrollingVariable;
												SGUILayout.InlineLabel("└   Variable", "Defines the scrolling uniform variable.\nBy default, a new property will be created for this texture, however you can use another texture's scrolling variable so that this texture will be linked with it.", highlighted);
												var scrollingVar = UseCustomScrollingVariable() ? ScrollingVariableLabel : GetScrollingVariableName();
												if (SGUILayout.ButtonPopup(scrollingVar))
												{
													var menu = new GenericMenu();
													string label = string.Format("{0}: {1}", ParentShaderProperty.Name, GetScrollingVariableName());
													if (ParentShaderProperty.Name == "_CustomMaterialPropertyDummy") // TODO get rid of the dummy shader property for custom material properties
													{
														label = GetScrollingVariableName();
													}

													menu.AddItem(new GUIContent(label), !UseCustomScrollingVariable(), () =>
													{
														ScrollingVariable = "";
														ScrollingVariableLabel = "";
														invalidScrollingVariable = false;
													});

													// fetch available scrolling values and add them to the menu
													var itemList = new List<MenuItem>();
													var availableValues = FetchValidScrollingValues();
													foreach (var availableValue in availableValues)
													{
														if (availableValue.label == this.Label)
														{
															continue;
														}

														if (string.IsNullOrEmpty(availableValue.disabled))
														{
															itemList.Add(new MenuItem()
															{
																guiContent = new GUIContent(string.Format("{0}: {1}", availableValue.label, availableValue.valueLabel)), // note: has non-breaking space character
																on = this.ScrollingVariable == availableValue.value,
																menuFunction = () =>
																{
																	ScrollingVariable = availableValue.value;
																	ScrollingVariableLabel = availableValue.valueLabel;
																	invalidScrollingVariable = false;
																}
															});
														}
														else
														{
															itemList.Add(new MenuItem()
															{
																guiContent = new GUIContent(string.Format("{0}: {1} {2}", availableValue.label, availableValue.valueLabel, availableValue.disabled)), // note: has non-breaking space character
																on = this.ScrollingVariable == availableValue.value,
																disabled = true
															});
														}
													}

													if (itemList.Count > 0)
													{
														menu.AddSeparator("");
														foreach (var item in itemList)
														{
															if (item.disabled)
															{
																menu.AddDisabledItem(item.guiContent);
															}
															else
															{
																menu.AddItem(item.guiContent, item.on, item.menuFunction);
															}
														}
													}

													menu.ShowAsContext();
												}
											}
											EndHorizontal();
										}
									}
								}
								else if (RandomOffset)
								{
									BeginHorizontal();
									{
										bool highlighted = !IsDefaultImplementation ? GlobalRandomOffset : GlobalRandomOffset != GetDefaultImplementation<Imp_MaterialProperty_Texture>().GlobalRandomOffset;
										SGUILayout.InlineLabel("└   Global", "Makes the random offset global to the selected UV coordinates: all textures using the same UV coordinates will inherit the random offset animation and values defined for this texture.\nIt also means that they will be calculated in the vertex shader (faster but uses one interpolator).", highlighted);
										GlobalRandomOffset = SGUILayout.Toggle(GlobalRandomOffset);
										GlobalScrolling = GlobalRandomOffset;
									}
									EndHorizontal();
								}
								else if (SineAnimation)
								{
									// Sine properties if any
								}
							}
						}

						bool showUvSinOptions = SineAnimation;
						if ((GlobalOptions.data.ShowDisabledFeatures || showUvSinOptions))
						{
							using (new EditorGUI.DisabledGroupScope(!showUvSinOptions))
							{
								BeginHorizontal();
								{
									bool highlighted = !IsDefaultImplementation ? GlobalSineAnimation : GlobalSineAnimation != GetDefaultImplementation<Imp_MaterialProperty_Texture>().GlobalSineAnimation;
									SGUILayout.InlineLabel("└   Global", "Makes the UV sin animation global to the selected UV coordinates: all textures using the same UV coordinates will inherit the sine animation and values defined for this texture.", highlighted);
									GlobalSineAnimation = SGUILayout.Toggle(GlobalSineAnimation);
									GlobalRandomOffset = GlobalScrolling = GlobalSineAnimation;
								}
								EndHorizontal();
								
								BeginHorizontal();
								{
									bool highlighted = !IsDefaultImplementation ? UseCustomSineAnimVariable() : SineAnimationVariable != GetDefaultImplementation<Imp_MaterialProperty_Texture>().SineAnimationVariable;
									SGUILayout.InlineLabel("└   Variable", "Defines the tiling/offset uniform variable.\nBy default, a new property will be created for this texture, however you can use another texture's tiling/offset variable so that this texture will be linked with it. You would typically do that if you have a normal map coupled with an albedo map, for example.", highlighted);
									var sinAnimVar = UseCustomSineAnimVariable() ? SineAnimationVariable : GetSineAnimVariableName();
									if (SGUILayout.ButtonPopup(sinAnimVar))
									{
										var menu = new GenericMenu();
										string label = string.Format("{0}: {1}", ParentShaderProperty.Name, GetSineAnimVariableName()); // note: has non-breaking space character
										if (ParentShaderProperty.Name == "_CustomMaterialPropertyDummy") // TODO get rid of the dummy shader property for custom material properties?
										{
											label = GetSineAnimVariableName();
										}

										menu.AddItem(new GUIContent(label), !UseCustomSineAnimVariable(), () =>
										{
											SineAnimationVariable = "";
											SineAnimationVariableLabel = "";
											invalidSinAnimVariable = false;
										});

										// fetch available tiling/offset values and add them to the menu
										var itemList = new List<MenuItem>();
										var availableValues = FetchValidSinAnimValues();
										foreach(var availableValue in availableValues)
										{
											if (availableValue.label == this.Label)
											{
												continue;
											}

											if (string.IsNullOrEmpty(availableValue.disabled))
											{
												itemList.Add(new MenuItem()
												{
													guiContent = new GUIContent(string.Format("{0}: {1}", availableValue.label, availableValue.valueLabel)), // note: has non-breaking space character
													on = this.SineAnimationVariable == availableValue.value,
													menuFunction = () =>
													{
														SineAnimationVariable = availableValue.value;
														SineAnimationVariableLabel = availableValue.valueLabel;
														invalidSinAnimVariable = false;
													}
												});
											}
											else
											{
												itemList.Add(new MenuItem()
												{
													guiContent = new GUIContent(string.Format("{0}: {1} {2}", availableValue.label, availableValue.valueLabel, availableValue.disabled)), // note: has non-breaking space character
													on = this.SineAnimationVariable == availableValue.value,
													disabled = true
												});
											}
										}

										if (itemList.Count > 0)
										{
											menu.AddSeparator("");
											foreach (var item in itemList)
											{
												if (item.disabled)
												{
													menu.AddDisabledItem(item.guiContent);
												}
												else
												{
													menu.AddItem(item.guiContent, item.on, item.menuFunction);
												}
											}
										}

										menu.ShowAsContext();
									}
								}
								EndHorizontal();
							}
						}

						using (new EditorGUI.DisabledGroupScope(program != ProgramType.Fragment && !IsCustomMaterialProperty))
						{
							BeginHorizontal();
							{
								bool highlighted = !IsDefaultImplementation ? NoTile : NoTile != GetDefaultImplementation<Imp_MaterialProperty_Texture>().NoTile;
								SGUILayout.InlineLabel("No Tile", "Use a special algorithm to prevent tile repetition", highlighted);
								NoTile = SGUILayout.Toggle(NoTile);
							}
							EndHorizontal();
						}

						if (NoTile && UseScrolling && GlobalScrolling)
						{
							TCP2_GUI.HelpBoxLayout("'Global Scrolling' and 'No Tile' don't work properly together: expect to see textures popping in their animation.", MessageType.Warning);
						}

						if (MipLevel >= 0 || IsCustomMaterialProperty)
						{
							BeginHorizontal();
							{
								bool highlighted = !IsDefaultImplementation ? MipLevel > 0 : MipLevel != GetDefaultImplementation<Imp_MaterialProperty_Texture>().MipLevel;
								SGUILayout.InlineLabel("Vertex Sampling Mip Level", highlighted);
								using (new EditorGUI.DisabledScope(MipProperty))
									MipLevel = SGUILayout.IntField(MipLevel, 0, 10);
							}
							EndHorizontal();

							BeginHorizontal();
							{
								bool highlighted = !IsDefaultImplementation ? MipProperty : MipProperty != GetDefaultImplementation<Imp_MaterialProperty_Texture>().MipProperty;
								SGUILayout.InlineLabel("└   Material Property", "Create a material property to control the mip level for sampling this texture in the vertex shader", highlighted);
								MipProperty = SGUILayout.Toggle(MipProperty);
							}
							EndHorizontal();
						}

						//SGUILayout.Indent -= 10;
					} // if ( uvExpanded )

					if (UvSource == UvSourceType.OtherShaderProperty)
					{
						BeginHorizontal();
						{
							bool highlighted = !IsDefaultImplementation ? false : LinkedShaderPropertyName != GetDefaultImplementation<Imp_MaterialProperty_Texture>().LinkedShaderPropertyName;
							SGUILayout.InlineLabel("UV Shader Property", highlighted);

							if (GUILayout.Button((LinkedShaderProperty != null) ? LinkedShaderProperty.Name : "None", SGUILayout.Styles.ShurikenPopup))
							{
								var menu = ShaderProperty.Imp_ShaderPropertyReference.CreateShaderPropertiesMenu(this.ParentShaderProperty, this.LinkedShaderProperty, OnSelectShaderProperty);
								if (menu != null)
								{
									menu.ShowAsContext();
								}
							}
						}
						EndHorizontal();
						GUILayout.Space(3);

						// linked shader property errors
						if (_linkedShaderProperty == null)
						{
							BeginHorizontal();
							{
								TCP2_GUI.HelpBoxLayout("No Shader Property defined.", MessageType.Error);
							}
							EndHorizontal();
						}
						else if (!_linkedShaderProperty.IsVisible())
						{
							BeginHorizontal();
							{
								TCP2_GUI.HelpBoxLayout("Invalid Shader Property defined.", MessageType.Error);
							}
							EndHorizontal();
						}
					}
					
					if (UvSource == UvSourceType.CustomMaterialProperty)
					{
						BeginHorizontal();
						{
							bool highlighted = !IsDefaultImplementation ? false : LinkedCustomMaterialPropertyName != GetDefaultImplementation<Imp_MaterialProperty_Texture>().LinkedCustomMaterialPropertyName;
							SGUILayout.InlineLabel("UV Custom Material Property", highlighted);

							if (GUILayout.Button((LinkedCustomMaterialProperty != null) ? LinkedCustomMaterialProperty.PropertyName : "None", SGUILayout.Styles.ShurikenPopup))
							{
								var menu = ShaderProperty.Imp_CustomMaterialProperty.CreateCustomMaterialPropertiesMenu(this.LinkedCustomMaterialProperty, OnSelectCustomMaterialProperty);
								if (menu != null)
								{
									menu.ShowAsContext();
								}
							}
						}
						EndHorizontal();
						GUILayout.Space(3);

						// linked shader property errors
						if (_linkedCustomMaterialProperty == null)
						{
							BeginHorizontal();
							{
								TCP2_GUI.HelpBoxLayout("No Custom Material Property defined.", MessageType.Error);
							}
							EndHorizontal();
						}
					}

					// errors

					if (UseTilingOffset && invalidTilingOffsetVariable)
					{
						BeginHorizontal();
						{
							TCP2_GUI.HelpBoxLayout("The UV Tiling/Offset Variable is invalid.\nMaybe the original source has been removed or can't be used anymore?", MessageType.Error);
						}
						EndHorizontal();
					}

					if (UseScrolling && invalidScrollingVariable)
					{
						BeginHorizontal();
						{
							TCP2_GUI.HelpBoxLayout("The UV Scrolling Variable is invalid.\nMaybe the original source has been removed or can't be used anymore?", MessageType.Error);
						}
						EndHorizontal();
					}
					
					if (SineAnimation && invalidSinAnimVariable)
					{
						BeginHorizontal();
						{
							TCP2_GUI.HelpBoxLayout("The UV Sin Animation variable is invalid.\nMaybe the original source has been removed or can't be used anymore?", MessageType.Error);
						}
						EndHorizontal();
					}
					
					if (InvalidSampler)
					{
						BeginHorizontal();
						{
							TCP2_GUI.HelpBoxLayout("The selected sampler is invalid.\nMaybe the original texture has been removed or can't be used anymore?", MessageType.Error);
						}
						EndHorizontal();
					}
					
				}
			}

			[Serialization.SerializeAs("imp_constant")]
			public class Imp_ConstantValue : Implementation
			{
				public static VariableType VariableCompatibility { get { return VariableTypeAll | VariableType.fixed_function_float; } }
				public static string MenuLabel { get { return "Constant Value"; } }
				internal override string GUILabel() { return MenuLabel; }

				[Serialization.SerializeAs("type"), ExcludeFromCopy] VariableType type;
				[Serialization.SerializeAs("fprc")] FloatPrecision floatPrec;

				[Serialization.SerializeAs("fv")] public float FloatValue = 1.0f;
				[Serialization.SerializeAs("f2v")] public Vector2 Float2Value = Vector2.one;
				[Serialization.SerializeAs("f3v")] public Vector3 Float3Value = Vector3.one;
				[Serialization.SerializeAs("f4v")] public Vector4 Float4Value = Vector4.one;
				[Serialization.SerializeAs("cv")] public Color ColorValue = Color.white;
				
				public Imp_ConstantValue(ShaderProperty shaderProperty) : base(shaderProperty)
				{
					type = shaderProperty.Type;
					floatPrec = FloatPrecision.@float;
				}

				internal override string PrintVariableFixedFunction()
				{
					return FloatValue.ToString();
				}

				internal override string PrintVariableFragment(string inputSource, string outputSource, string arguments)
				{
					switch (type)
					{
						case VariableType.@float: return FloatValue.ToString("#.0###############", CultureInfo.InvariantCulture);
						case VariableType.float2: return string.Format(CultureInfo.InvariantCulture, "{0}2({1},{2})", floatPrec, Float2Value.x, Float2Value.y);
						case VariableType.float3: return string.Format(CultureInfo.InvariantCulture, "{0}3({1},{2},{3})", floatPrec, Float3Value.x, Float3Value.y, Float3Value.z);
						case VariableType.float4: return string.Format(CultureInfo.InvariantCulture, "{0}4({1},{2},{3},{4})", floatPrec, Float4Value.x, Float4Value.y, Float4Value.z, Float4Value.w);
						case VariableType.color: return string.Format(CultureInfo.InvariantCulture, "{0}3({1},{2},{3})", floatPrec, ColorValue.r, ColorValue.g, ColorValue.b);
						case VariableType.color_rgba: return string.Format(CultureInfo.InvariantCulture, "{0}4({1},{2},{3},{4})", floatPrec, ColorValue.r, ColorValue.g, ColorValue.b, ColorValue.a);
					}

					return null;
				}

				internal override void NewLineGUI(bool usedByCustomCode)
				{
					BeginHorizontal();
					ShaderGenerator2.ContextualHelpBox("Uses a constant value in the shader.\nIf your shader property will keep the same value, this will be faster than using a Material Property.");
					EndHorizontal();

					BeginHorizontal();
					{
						bool highlighted = false;
						switch(type)
						{
							case VariableType.@float:
							case VariableType.fixed_function_float:
								highlighted = !IsDefaultImplementation ? FloatValue != 1.0f : FloatValue != GetDefaultImplementation<Imp_ConstantValue>().FloatValue;
								break;

							case VariableType.float2:
								highlighted = !IsDefaultImplementation ? Float2Value != Vector2.one : Float2Value != GetDefaultImplementation<Imp_ConstantValue>().Float2Value;
								break;
							case VariableType.float3:
								highlighted = !IsDefaultImplementation ? Float3Value != Vector3.one : Float3Value != GetDefaultImplementation<Imp_ConstantValue>().Float3Value;
								break;
							case VariableType.float4:
								highlighted = !IsDefaultImplementation ? Float4Value != Vector4.one : Float4Value != GetDefaultImplementation<Imp_ConstantValue>().Float4Value;
								break;
							case VariableType.color:
							case VariableType.color_rgba:
								highlighted = !IsDefaultImplementation ? ColorValue != Color.white : ColorValue != GetDefaultImplementation<Imp_ConstantValue>().ColorValue;
								break;
						}

						SGUILayout.InlineLabel("Value", highlighted);

						switch (type)
						{
							case VariableType.@float:
							case VariableType.fixed_function_float:
								FloatValue = SGUILayout.FloatField(FloatValue);
								break;
							case VariableType.float2:
								Float2Value = SGUILayout.Vector2Field(Float2Value);
								break;
							case VariableType.float3:
								Float3Value = SGUILayout.Vector3Field(Float3Value);
								break;
							case VariableType.float4:
								Float4Value = SGUILayout.Vector4Field(Float4Value);
								break;
							case VariableType.color:
								ColorValue = SGUILayout.ColorField(ColorValue, false, floatPrec != FloatPrecision.@fixed);
								break;
							case VariableType.color_rgba:
								ColorValue = SGUILayout.ColorField(ColorValue, true, floatPrec != FloatPrecision.@fixed);
								break;
						}
					}
					EndHorizontal();

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? floatPrec != default(FloatPrecision) : floatPrec != GetDefaultImplementation<Imp_ConstantValue>().floatPrec;
						SGUILayout.InlineLabel("Precision", highlighted);
						floatPrec = (FloatPrecision)SGUILayout.EnumPopup(floatPrec);
					}
					EndHorizontal();
				}
			}

			[Serialization.SerializeAs("imp_constant_float")]
			public class Imp_ConstantFloat : Implementation
			{
				public static VariableType VariableCompatibility { get { return VariableTypeAll; } }
				public static string MenuLabel { get { return "Constant Float"; } }
				internal override string GUILabel() { return MenuLabel; }

				[Serialization.SerializeAs("fprc")] FloatPrecision floatPrec;

				[Serialization.SerializeAs("fv")] public float FloatValue = 1.0f;

				public Imp_ConstantFloat(ShaderProperty shaderProperty) : base(shaderProperty)
				{
					floatPrec = FloatPrecision.@float;
				}

				internal override string PrintVariableFixedFunction()
				{
					return FloatValue.ToString();
				}

				internal override string PrintVariableFragment(string inputSource, string outputSource, string arguments)
				{
					return FloatValue.ToString("#.0###############", CultureInfo.InvariantCulture);
				}

				internal override void NewLineGUI(bool usedByCustomCode)
				{
					BeginHorizontal();
					ShaderGenerator2.ContextualHelpBox("Uses a constant value in the shader.\nIf your shader property will keep the same value, this will be faster than using a Material Property.");
					EndHorizontal();

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? FloatValue != 1.0f : FloatValue != GetDefaultImplementation<Imp_ConstantFloat>().FloatValue;
						SGUILayout.InlineLabel("Value", highlighted);
						FloatValue = SGUILayout.FloatField(FloatValue);
					}
					EndHorizontal();

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? floatPrec != default(FloatPrecision) : floatPrec != GetDefaultImplementation<Imp_ConstantFloat>().floatPrec;
						SGUILayout.InlineLabel("Precision", highlighted);
						floatPrec = (FloatPrecision)SGUILayout.EnumPopup(floatPrec);
					}
					EndHorizontal();
				}
			}

			[Serialization.SerializeAs("imp_vcolors")]
			public class Imp_VertexColor : Implementation
			{
				public static VariableType VariableCompatibility { get { return VariableTypeAll; } }
				public static string MenuLabel { get { return "Vertex/Color"; } }
				internal override string GUILabel() { return MenuLabel; }
				internal override OptionFeatures[] NeededFeatures() { return new[] { OptionFeatures.VertexColors }; }

				[Serialization.SerializeAs("cc")] public int ChannelsCount = 3;
				[Serialization.SerializeAs("chan")] public string Channels = "RGB";
				string DefaultChannels = "RGB";
				[Serialization.SerializeAs("linear")] public bool ConvertToLinearSpace = false;

				public Imp_VertexColor(ShaderProperty shaderProperty) : base(shaderProperty)
				{
					InitChannelsCount();
					InitChannelsSwizzle();
				}

				void InitChannelsCount()
				{
					switch (ParentShaderProperty.Type)
					{
						case VariableType.@float: ChannelsCount = 1; break;
						case VariableType.float2: ChannelsCount = 2; break;
						case VariableType.color:
						case VariableType.float3: ChannelsCount = 3; break;
						case VariableType.color_rgba:
						case VariableType.float4: ChannelsCount = 4; break;
					}
				}

				void InitChannelsSwizzle()
				{
					switch (ParentShaderProperty.Type)
					{
						case VariableType.@float: Channels = "R"; break;
						case VariableType.float2: Channels = "RG"; break;
						case VariableType.color:
						case VariableType.float3: Channels = "RGB"; break;
						case VariableType.color_rgba:
						case VariableType.float4: Channels = "RGBA"; break;
					}
					DefaultChannels = Channels;
				}

				public override void OnPasted()
				{
					InitChannelsCount();
				}

				internal override string PrintVariableFragment(string inputSource, string outputSource, string arguments)
				{
					var hideChannels = TryGetArgument("hide_channels", arguments);
					var channels = string.IsNullOrEmpty(hideChannels) ? "." + Channels.ToLowerInvariant() : "";
					var vertexColorsVariable = $"{inputSource}.vertexColor";
					if (ConvertToLinearSpace)
					{
						if (ShaderGenerator2.IsURP)
							vertexColorsVariable = $"SRGBToLinear({vertexColorsVariable})";
						else
							vertexColorsVariable = $"half4(GammaToLinearSpace({vertexColorsVariable}.rgb), {vertexColorsVariable}.a)";
					}
					return string.Format($"{vertexColorsVariable}{channels}");
				}

				internal override void NewLineGUI(bool usedByCustomCode)
				{
					BeginHorizontal();
					ShaderGenerator2.ContextualHelpBox("Fetch the mesh's vertex colors.");
					EndHorizontal();

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? Channels != DefaultChannels : Channels != GetDefaultImplementation<Imp_VertexColor>().Channels;
						SGUILayout.InlineLabel("Swizzle", highlighted);

						if (usedByCustomCode)
						{
							using (new EditorGUI.DisabledScope(true))
							{
								GUILayout.Label(TCP2_GUI.TempContent("Defined in Custom Code"), SGUILayout.Styles.ShurikenValue, GUILayout.Height(16), GUILayout.ExpandWidth(false));
							}
						}
						else
						{
							if (ChannelsCount == 1)
								Channels = SGUILayout.RGBASelector(Channels);
							else
								Channels = SGUILayout.RGBASwizzle(Channels, ChannelsCount);
						}
					}
					EndHorizontal();

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? ConvertToLinearSpace : ConvertToLinearSpace != GetDefaultImplementation<Imp_VertexColor>().ConvertToLinearSpace;
						SGUILayout.InlineLabel(TCP2_GUI.TempContent("Convert to Linear Space", "Convert the vertex colors to linear color space if the project is in linear color space."), highlighted);
						ConvertToLinearSpace = SGUILayout.Toggle(ConvertToLinearSpace);
					}
					EndHorizontal();
				}
			}

			[Serialization.SerializeAs("imp_texcoord")]
			public class Imp_VertexTexcoord : Implementation
			{
				public static VariableType VariableCompatibility { get { return VariableTypeAll; } }
				public static string MenuLabel { get { return "Vertex/UV"; } }
				internal override string GUILabel() { return MenuLabel; }

				[Serialization.SerializeAs("tex")] public int TexcoordChannel = 0;
				[Serialization.SerializeAs("cc")] public int ChannelsCount = 3;
				[Serialization.SerializeAs("chan")] public string Channels = "XYZ";
				string DefaultChannels = "XYZ";

				public Imp_VertexTexcoord(ShaderProperty shaderProperty) : base(shaderProperty)
				{
					InitChannelsCount();
					InitChannelsSwizzle();
				}

				void InitChannelsCount()
				{
					switch (ParentShaderProperty.Type)
					{
						case VariableType.@float: ChannelsCount = 1; break;
						case VariableType.float2: ChannelsCount = 2; break;
						case VariableType.color:
						case VariableType.float3: ChannelsCount = 3; break;
						case VariableType.color_rgba:
						case VariableType.float4: ChannelsCount = 4; break;
					}
				}

				void InitChannelsSwizzle()
				{
					switch (ParentShaderProperty.Type)
					{
						case VariableType.@float: Channels = "X"; break;
						case VariableType.float2: Channels = "XY"; break;
						case VariableType.color:
						case VariableType.float3: Channels = "XYZ"; break;
						case VariableType.color_rgba:
						case VariableType.float4: Channels = "XYZW"; break;
					}
					DefaultChannels = Channels;
				}

				public override void OnPasted()
				{
					InitChannelsCount();
				}

				internal override string PrintVariableVertex(string inputSource, string outputSource, string arguments)
				{
					return $"{inputSource}.{"texcoord" + TexcoordChannel}.{Channels.ToLowerInvariant()}";
				}

				internal override string PrintVariableFragment(string inputSource, string outputSource, string arguments)
				{
					string coord = ShaderGenerator2.VariablesManager.GetVariable("texcoord" + TexcoordChannel);
					if (string.IsNullOrEmpty(coord))
					{
						Debug.LogError(ShaderGenerator2.ErrorMsg("Can't find UV coordinates for Shader Property: " + ParentShaderProperty.Name));
						return null;
					}
					else
					{
						bool usedByCustomCode = this.ParentShaderProperty.IsImplementationUsedInCustomCode(this);
						if (usedByCustomCode)
							return $"{inputSource}.{coord}";
						else
							return $"{inputSource}.{coord}.{Channels.ToLowerInvariant()}";
					}

					//var hideChannels = TryGetArgument("hide_channels", arguments);
					//var channels = string.IsNullOrEmpty(hideChannels) ? "." + Channels.ToLowerInvariant() : "";
					//return string.Format("{0}.texcoord{1}{2}", inputSource, TexcoordChannel, channels);
				}

				internal override void NewLineGUI(bool usedByCustomCode)
				{
					BeginHorizontal();
					ShaderGenerator2.ContextualHelpBox("Fetch the mesh's specified UV coordinates.");
					EndHorizontal();

					BeginHorizontal();
					{
						EditorGUI.BeginChangeCheck();
						bool highlighted = !IsDefaultImplementation ? TexcoordChannel  > 0 : TexcoordChannel != GetDefaultImplementation<Imp_VertexTexcoord>().TexcoordChannel;
						SGUILayout.InlineLabel("UV Channel", highlighted);
						char newTecoordChannel = SGUILayout.GenericSelector("0123", (char)(TexcoordChannel + '0'));
						if (EditorGUI.EndChangeCheck())
						{
							TexcoordChannel = newTecoordChannel - '0';
						}
					}
					EndHorizontal();

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? Channels != DefaultChannels : Channels != GetDefaultImplementation<Imp_VertexTexcoord>().Channels;
						SGUILayout.InlineLabel("Swizzle", highlighted);

						if (usedByCustomCode)
						{
							using (new EditorGUI.DisabledScope(true))
							{
								GUILayout.Label(TCP2_GUI.TempContent("Defined in Custom Code"), SGUILayout.Styles.ShurikenValue, GUILayout.Height(16), GUILayout.ExpandWidth(false));
							}
						}
						else
						{
							if (ChannelsCount == 1)
								Channels = SGUILayout.XYZWSelector(Channels);
							else
								Channels = SGUILayout.XYZWSwizzle(Channels, ChannelsCount);
						}
					}
					EndHorizontal();
				}
			}

			[Serialization.SerializeAs("imp_localpos")]
			public class Imp_LocalPosition : Implementation
			{
				public static VariableType VariableCompatibility { get { return VariableTypeAll; } }
				public static string MenuLabel { get { return "Vertex/Local Position"; } }
				internal override string GUILabel() { return MenuLabel; }
				internal override OptionFeatures[] NeededFeatures()
				{
					return ParentShaderProperty.Program == ProgramType.Fragment ? new[] { OptionFeatures.Local_Pos_Fragment } : new OptionFeatures[0];
				}

				[Serialization.SerializeAs("cc")] public int ChannelsCount = 3;
				[Serialization.SerializeAs("chan")] public string Channels = "XYZ";
				string DefaultChannels = "XYZ";

				public Imp_LocalPosition(ShaderProperty shaderProperty) : base(shaderProperty)
				{
					InitChannelsCount();
					InitChannelsSwizzle();
				}

				void InitChannelsCount()
				{
					switch (ParentShaderProperty.Type)
					{
						case VariableType.@float: ChannelsCount = 1; break;
						case VariableType.float2: ChannelsCount = 2; break;
						case VariableType.color:
						case VariableType.float3: ChannelsCount = 3; break;
						case VariableType.color_rgba:
						case VariableType.float4: ChannelsCount = 4; break;
					}
				}

				void InitChannelsSwizzle()
				{
					switch (ParentShaderProperty.Type)
					{
						case VariableType.@float: Channels = "X"; break;
						case VariableType.float2: Channels = "XY"; break;
						case VariableType.color:
						case VariableType.float3: Channels = "XYZ"; break;
						case VariableType.color_rgba:
						case VariableType.float4: Channels = "XYZW"; break;
					}
					DefaultChannels = Channels;
				}

				public override void OnPasted()
				{
					InitChannelsCount();
				}

				internal override string PrintVariableVertex(string inputSource, string outputSource, string arguments)
				{
					var hideChannels = TryGetArgument("hide_channels", arguments);
					var channels = string.IsNullOrEmpty(hideChannels) ? "." + Channels.ToLowerInvariant() : "";
					return string.Format("{0}.vertex{1}", inputSource, channels);
				}

				internal override string PrintVariableFragment(string inputSource, string outputSource, string arguments)
				{
					var hideChannels = TryGetArgument("hide_channels", arguments);
					var channels = string.IsNullOrEmpty(hideChannels) ? "." + Channels.ToLowerInvariant() : "";
					return string.Format("{0}.[[INPUT_VALUE:objPos]]{1}", inputSource, channels);
				}

				internal override void NewLineGUI(bool usedByCustomCode)
				{
					BeginHorizontal();
					ShaderGenerator2.ContextualHelpBox("The object space position for the current vertex or fragment.");
					EndHorizontal();

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? Channels != DefaultChannels : Channels != GetDefaultImplementation<Imp_LocalPosition>().Channels;
						SGUILayout.InlineLabel("Swizzle", highlighted);

						if (usedByCustomCode)
						{
							using (new EditorGUI.DisabledScope(true))
							{
								GUILayout.Label(TCP2_GUI.TempContent("Defined in Custom Code"), SGUILayout.Styles.ShurikenValue, GUILayout.Height(16), GUILayout.ExpandWidth(false));
							}
						}
						else
						{
							if (ChannelsCount == 1)
								Channels = SGUILayout.XYZSelector(Channels);
							else
								Channels = SGUILayout.XYZSwizzle(Channels, ChannelsCount);
						}
					}
					EndHorizontal();
				}
			}

			[Serialization.SerializeAs("imp_worldpos")]
			public class Imp_WorldPosition : Implementation
			{
				public static VariableType VariableCompatibility { get { return VariableTypeAll; } }
				public static string MenuLabel { get { return "Vertex/World Position"; } }
				internal override string GUILabel() { return MenuLabel; }
				internal override OptionFeatures[] NeededFeatures() { return new[] { ParentShaderProperty.Program == ProgramType.Vertex ? OptionFeatures.World_Pos_UV_Vertex : OptionFeatures.World_Pos_UV_Fragment }; }

				[Serialization.SerializeAs("cc")] public int ChannelsCount = 3;
				[Serialization.SerializeAs("chan")] public string Channels = "XYZ";
				string DefaultChannels = "XYZ";

				public Imp_WorldPosition(ShaderProperty shaderProperty) : base(shaderProperty)
				{
					InitChannelsCount();
					InitChannelsSwizzle();
				}

				void InitChannelsCount()
				{
					switch (ParentShaderProperty.Type)
					{
						case VariableType.@float: ChannelsCount = 1; break;
						case VariableType.float2: ChannelsCount = 2; break;
						case VariableType.color:
						case VariableType.float3: ChannelsCount = 3; break;
						case VariableType.color_rgba:
						case VariableType.float4: ChannelsCount = 4; break;
					}
				}

				void InitChannelsSwizzle()
				{
					switch (ParentShaderProperty.Type)
					{
						case VariableType.@float: Channels = "X"; break;
						case VariableType.float2: Channels = "XY"; break;
						case VariableType.color:
						case VariableType.float3: Channels = "XYZ"; break;
						case VariableType.color_rgba:
						case VariableType.float4: Channels = "XYZW"; break;
					}
					DefaultChannels = Channels;
				}

				public override void OnPasted()
				{
					InitChannelsCount();
				}

				internal override string PrintVariableVertex(string inputSource, string outputSource, string arguments)
				{
					var hideChannels = TryGetArgument("hide_channels", arguments);
					var channels = string.IsNullOrEmpty(hideChannels) ? "." + Channels.ToLowerInvariant() : "";
					return string.Format("worldPosUv{1}", inputSource, channels);
				}

				internal override string PrintVariableFragment(string inputSource, string outputSource, string arguments)
				{
					var hideChannels = TryGetArgument("hide_channels", arguments);
					var channels = string.IsNullOrEmpty(hideChannels) ? "." + Channels.ToLowerInvariant() : "";
					return string.Format("{0}{1}", ShaderGenerator2.IsURP ? "positionWS" : inputSource + ".worldPos", channels);
				}

				internal override void NewLineGUI(bool usedByCustomCode)
				{
					BeginHorizontal();
					ShaderGenerator2.ContextualHelpBox("The world space position for the current vertex or fragment.");
					EndHorizontal();

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? Channels != DefaultChannels : Channels != GetDefaultImplementation<Imp_WorldPosition>().Channels;
						SGUILayout.InlineLabel("Swizzle", highlighted);

						if (usedByCustomCode)
						{
							using (new EditorGUI.DisabledScope(true))
							{
								GUILayout.Label(TCP2_GUI.TempContent("Defined in Custom Code"), SGUILayout.Styles.ShurikenValue, GUILayout.Height(16), GUILayout.ExpandWidth(false));
							}
						}
						else
						{
							if (ChannelsCount == 1)
								Channels = SGUILayout.XYZSelector(Channels);
							else
								Channels = SGUILayout.XYZSwizzle(Channels, ChannelsCount);
						}
					}
					EndHorizontal();
				}
			}

			[Serialization.SerializeAs("imp_objworldpos")]
			public class Imp_ObjectWorldPosition : Implementation
			{
				public static VariableType VariableCompatibility { get { return VariableTypeAll; } }
				public static string MenuLabel { get { return "Vertex/Mesh World Position"; } }
				internal override string GUILabel() { return MenuLabel; }
				internal override OptionFeatures[] NeededFeatures() { return new OptionFeatures[0]; }

				[Serialization.SerializeAs("cc")] public int ChannelsCount = 3;
				[Serialization.SerializeAs("chan")] public string Channels = "XYZ";
				string DefaultChannels = "XYZ";

				public Imp_ObjectWorldPosition(ShaderProperty shaderProperty) : base(shaderProperty)
				{
					InitChannelsCount();
					InitChannelsSwizzle();
				}

				void InitChannelsCount()
				{
					switch (ParentShaderProperty.Type)
					{
						case VariableType.@float: ChannelsCount = 1; break;
						case VariableType.float2: ChannelsCount = 2; break;
						case VariableType.color:
						case VariableType.float3: ChannelsCount = 3; break;
						case VariableType.color_rgba:
						case VariableType.float4: ChannelsCount = 4; break;
					}
				}

				void InitChannelsSwizzle()
				{
					switch (ParentShaderProperty.Type)
					{
						case VariableType.@float: Channels = "X"; break;
						case VariableType.float2: Channels = "XY"; break;
						case VariableType.color:
						case VariableType.float3: Channels = "XYZ"; break;
						case VariableType.color_rgba:
						case VariableType.float4: Channels = "XYZW"; break;
					}
					DefaultChannels = Channels;
				}

				public override void OnPasted()
				{
					InitChannelsCount();
				}

				internal override string PrintVariableVertex(string inputSource, string outputSource, string arguments)
				{
					var hideChannels = TryGetArgument("hide_channels", arguments);
					var channels = string.IsNullOrEmpty(hideChannels) ? "." + Channels.ToLowerInvariant() : "";
					return string.Format("unity_ObjectToWorld._m03_m13_m23{1}", inputSource, channels);
				}

				internal override string PrintVariableFragment(string inputSource, string outputSource, string arguments)
				{
					var hideChannels = TryGetArgument("hide_channels", arguments);
					var channels = string.IsNullOrEmpty(hideChannels) ? "." + Channels.ToLowerInvariant() : "";
					return $"unity_ObjectToWorld._m03_m13_m23{channels}";
				}

				internal override void NewLineGUI(bool usedByCustomCode)
				{
					BeginHorizontal();
					ShaderGenerator2.ContextualHelpBox("The world space position for the whole mesh.");
					EndHorizontal();

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? Channels != DefaultChannels : Channels != GetDefaultImplementation<Imp_ObjectWorldPosition>().Channels;
						SGUILayout.InlineLabel("Swizzle", highlighted);

						if (usedByCustomCode)
						{
							using (new EditorGUI.DisabledScope(true))
							{
								GUILayout.Label(TCP2_GUI.TempContent("Defined in Custom Code"), SGUILayout.Styles.ShurikenValue, GUILayout.Height(16), GUILayout.ExpandWidth(false));
							}
						}
						else
						{
							if (ChannelsCount == 1)
								Channels = SGUILayout.XYZSelector(Channels);
							else
								Channels = SGUILayout.XYZSwizzle(Channels, ChannelsCount);
						}
					}
					EndHorizontal();
				}
			}

			[Serialization.SerializeAs("imp_localnorm")]
			public class Imp_LocalNormal: Implementation
			{
				public static VariableType VariableCompatibility { get { return VariableTypeAll; } }
				public static string MenuLabel { get { return "Vertex/Local Normal"; } }
				internal override string GUILabel() { return MenuLabel; }
				internal override OptionFeatures[] NeededFeatures()
				{
					return ParentShaderProperty.Program == ProgramType.Fragment ? new[] { OptionFeatures.Local_Normal_Fragment } : new OptionFeatures[0];
				}

				[Serialization.SerializeAs("cc")] public int ChannelsCount = 3;
				[Serialization.SerializeAs("chan")] public string Channels = "XYZ";
				string DefaultChannels = "XYZ";

				public Imp_LocalNormal(ShaderProperty shaderProperty) : base(shaderProperty)
				{
					InitChannelsCount();
					InitChannelsSwizzle();
				}

				void InitChannelsCount()
				{
					switch (ParentShaderProperty.Type)
					{
						case VariableType.@float: ChannelsCount = 1; break;
						case VariableType.float2: ChannelsCount = 2; break;
						case VariableType.color:
						case VariableType.float3: ChannelsCount = 3; break;
						case VariableType.color_rgba:
						case VariableType.float4: ChannelsCount = 4; break;
					}
				}

				void InitChannelsSwizzle()
				{
					switch (ParentShaderProperty.Type)
					{
						case VariableType.@float: Channels = "X"; break;
						case VariableType.float2: Channels = "XY"; break;
						case VariableType.color:
						case VariableType.float3: Channels = "XYZ"; break;
						case VariableType.color_rgba:
						case VariableType.float4: Channels = "XYZW"; break;
					}
					DefaultChannels = Channels;
				}

				public override void OnPasted()
				{
					InitChannelsCount();
				}

				internal override string PrintVariableVertex(string inputSource, string outputSource, string arguments)
				{
					var hideChannels = TryGetArgument("hide_channels", arguments);
					var channels = string.IsNullOrEmpty(hideChannels) ? "." + Channels.ToLowerInvariant() : "";
					return string.Format("{0}.normal{1}", inputSource, channels);
				}

				internal override string PrintVariableFragment(string inputSource, string outputSource, string arguments)
				{
					var hideChannels = TryGetArgument("hide_channels", arguments);
					var channels = string.IsNullOrEmpty(hideChannels) ? "." + Channels.ToLowerInvariant() : "";
					return string.Format("{0}.[[INPUT_VALUE:objNormal]]{1}", inputSource, channels);
				}

				internal override void NewLineGUI(bool usedByCustomCode)
				{
					BeginHorizontal();
					ShaderGenerator2.ContextualHelpBox("The object space position for the current vertex or fragment.");
					EndHorizontal();

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? Channels != DefaultChannels : Channels != GetDefaultImplementation<Imp_LocalNormal>().Channels;
						SGUILayout.InlineLabel("Swizzle", highlighted);

						if (usedByCustomCode)
						{
							using (new EditorGUI.DisabledScope(true))
							{
								GUILayout.Label(TCP2_GUI.TempContent("Defined in Custom Code"), SGUILayout.Styles.ShurikenValue, GUILayout.Height(16), GUILayout.ExpandWidth(false));
							}
						}
						else
						{
							if (ChannelsCount == 1)
								Channels = SGUILayout.XYZSelector(Channels);
							else
								Channels = SGUILayout.XYZSwizzle(Channels, ChannelsCount);
						}
					}
					EndHorizontal();
				}
			}

			[Serialization.SerializeAs("imp_worldnorm")]
			public class Imp_WorldNormal : Implementation
			{
				public static VariableType VariableCompatibility { get { return VariableTypeAll; } }
				public static string MenuLabel { get { return "Vertex/World Normal"; } }
				internal override string GUILabel() { return MenuLabel; }

				internal override OptionFeatures[] NeededFeatures()
				{
					return new[] {ParentShaderProperty.Program == ProgramType.Vertex ? OptionFeatures.World_Normal_Vertex : OptionFeatures.World_Normal_Fragment};
				}

				[Serialization.SerializeAs("cc")] public int ChannelsCount = 3;
				[Serialization.SerializeAs("chan")] public string Channels = "XYZ";
				string DefaultChannels = "XYZ";

				public Imp_WorldNormal(ShaderProperty shaderProperty) : base(shaderProperty)
				{
					InitChannelsCount();
					InitChannelsSwizzle();
				}

				void InitChannelsCount()
				{
					switch (ParentShaderProperty.Type)
					{
						case VariableType.@float: ChannelsCount = 1; break;
						case VariableType.float2: ChannelsCount = 2; break;
						case VariableType.color:
						case VariableType.float3: ChannelsCount = 3; break;
						case VariableType.color_rgba:
						case VariableType.float4: ChannelsCount = 4; break;
					}
				}

				void InitChannelsSwizzle()
				{
					switch (ParentShaderProperty.Type)
					{
						case VariableType.@float: Channels = "X"; break;
						case VariableType.float2: Channels = "XY"; break;
						case VariableType.color:
						case VariableType.float3: Channels = "XYZ"; break;
						case VariableType.color_rgba:
						case VariableType.float4: Channels = "XYZW"; break;
					}
					DefaultChannels = Channels;
				}

				public override void OnPasted()
				{
					InitChannelsCount();
				}

				internal override string PrintVariableVertex(string inputSource, string outputSource, string arguments)
				{
					var hideChannels = TryGetArgument("hide_channels", arguments);
					var channels = string.IsNullOrEmpty(hideChannels) ? "." + Channels.ToLowerInvariant() : "";
					return string.Format("worldNormalUv{0}", channels);
				}

				internal override string PrintVariableFragment(string inputSource, string outputSource, string arguments)
				{
					var hideChannels = TryGetArgument("hide_channels", arguments);
					var channels = string.IsNullOrEmpty(hideChannels) ? "." + Channels.ToLowerInvariant() : "";

					if (ShaderGenerator2.IsURP)
					{
						return string.Format("normalWS{0}", channels);
					}
					else
					{
						return string.Format("{0}.[[INPUT_VALUE:worldNormal]]{1}", inputSource, channels);
					}
				}

				internal override void NewLineGUI(bool usedByCustomCode)
				{
					BeginHorizontal();
					ShaderGenerator2.ContextualHelpBox("The world space normal for the current vertex or fragment.");
					EndHorizontal();

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? Channels != DefaultChannels : Channels != GetDefaultImplementation<Imp_WorldNormal>().Channels;
						SGUILayout.InlineLabel("Swizzle", highlighted);

						if (usedByCustomCode)
						{
							using (new EditorGUI.DisabledScope(true))
							{
								GUILayout.Label(TCP2_GUI.TempContent("Defined in Custom Code"), SGUILayout.Styles.ShurikenValue, GUILayout.Height(16), GUILayout.ExpandWidth(false));
							}
						}
						else
						{
							if (ChannelsCount == 1)
								Channels = SGUILayout.XYZSelector(Channels);
							else
								Channels = SGUILayout.XYZSwizzle(Channels, ChannelsCount);
						}
					}
					EndHorizontal();
				}
			}

			// Generic Implementation that is generated inside the Templates.
			// Originally made to add support for NDL, NDV implementations.
			[Serialization.SerializeAs("imp_generic")]
			public class Imp_GenericFromTemplate : Implementation
			{
				/// <summary>
				/// Represents a Generic Implementation model defined from a template
				/// </summary>
				public struct GenericImplementation
				{
					public bool valid; // replacement for null checks
					public string identifier;
					public bool available;
					public int pass;
					public string MenuLabel;
					public string HelpMessage;
					public string WarningMessage;
					public VariableType Compatibility;
					public string VariableName;
					public string ChannelsOptions;
					public string NeededFeatures;
					public string Options;
					public List<ShaderProperty> compatibleShaderProperties;
					public bool WorksWithCustomCode;

					public Imp_GenericFromTemplate CreateImplementation(ShaderProperty shaderProperty)
					{
						var imp = new Imp_GenericFromTemplate(shaderProperty);

						// copy properties
						imp.ChannelsOptions = this.ChannelsOptions;
						imp.MenuLabel = this.MenuLabel;
						imp.HelpMessage = this.HelpMessage;
						imp.WarningMessage = this.WarningMessage;
						imp.Compatibility = this.Compatibility;
						imp.VariableName = this.VariableName;
						imp.NeededFeaturesStr = this.NeededFeatures;
						imp.WorksWithCustomCode = this.WorksWithCustomCode;
						imp.OptionsString = this.Options;
						imp.ParseOptions();

						// identification based on available Generic Implementations
						imp.sourceIdentifier = this.identifier;
						imp.sourceIsAvailable = true;
						imp.Register();

						imp.DeduceChannelsSettings(shaderProperty);

						return imp;
					}
				}

				// List of currently available generic implementations parsed from the current template
				public static List<GenericImplementation> AvailableGenericImplementations;

				public static void InitList()
				{
					AvailableGenericImplementations = new List<GenericImplementation>();
				}

				public static void EnableFromLine(string line, int pass, string program)
				{
					// format example: #ENABLE_IMPL: float ndl, "Special/N·L (diffuse lighting)", all
					string lineWithoutHeader = line.Substring(line.IndexOf(':')+1).Trim();
					string[] data = Serialization.SplitExcludingBlocks(lineWithoutHeader, ',', true);

					string id = data[0] + pass + program;

					// enable existing
					var existing = AvailableGenericImplementations.Find(x => x.identifier == id);
					if (existing.valid)
					{
						existing.available = true;
						return;
					}

					// create and enable new

					// - first data is "type name"
					int space = data[0].IndexOf(' ');
					string type = data[0].Substring(0, space);
					string name = data[0].Substring(space+1);

					string label = "No Label";
					string compatibility = "all";
					string help = null;
					string warning = null;
					string neededFeatures = "";
					string options = "";
					bool customCodeCompatible = false;

					// - remaining data is "key = value" pairs
					for (int i = 1; i < data.Length; i++)
					{
						var subdata = data[i].Split('=');
						var subdata1 = subdata[1].Trim(' ', '"');
						switch (subdata[0].Trim())
						{
							case "lbl": label = subdata1; break;
							case "compat": compatibility = subdata1; break;
							case "help": help = subdata1; break;
							case "warning": warning = subdata1; break;
							case "toggles": neededFeatures = subdata1; break;
							case "options": options = subdata1; break;
							case "custom_code_compatible": customCodeCompatible = bool.Parse(subdata1); break;
						}
					}

					var imp = new GenericImplementation()
					{
						valid = true,
						identifier = data[0] + pass + program,
						available = true,
						pass = pass,
						MenuLabel = label,
						HelpMessage = help,
						WarningMessage = warning,
						Compatibility = (compatibility == "all") ? VariableTypeAll : (VariableType)Enum.Parse(typeof(VariableType), compatibility),
						VariableName = name,
						ChannelsOptions = GetChannelsOption(type),
						NeededFeatures = neededFeatures,
						Options = options,
						compatibleShaderProperties = new List<ShaderProperty>(),
						WorksWithCustomCode = customCodeCompatible

					};
					AvailableGenericImplementations.Add(imp);
				}

				static string GetChannelsOption(string type)
				{
					switch (type)
					{
						default:
							Debug.LogError("Invalid type for channels: " + type);
							break;
						case "float": return "X";
						case "float2": return "XY";
						case "float3": return "XYZ";
						case "float4": return "XYZW";
						case "color": return "RGB";
						case "color_rgba": return "RGBA";
					}
					return null;
				}

				public static void DisableFromLine(string line, int pass, string program)
				{
					bool found = false;
					var id = line.Substring(line.IndexOf(':')+1).Trim() + pass + program;

					for (int i = 0; i < AvailableGenericImplementations.Count; i++)
					{
						var imp = AvailableGenericImplementations[i];

						if (imp.identifier == id)
						{
							imp.available = false;
							AvailableGenericImplementations[i] = imp;
							found = true;
						}
					}

					if (!found)
					{
						Debug.LogWarning(ShaderGenerator2.ErrorMsg("Can't disable Generic Implementation with this identifier: " + id));
					}
				}

				public static void DisableAll()
				{
					for (int i = 0; i < AvailableGenericImplementations.Count; i++)
					{
						var imp = AvailableGenericImplementations[i];
						imp.available = false;
						AvailableGenericImplementations[i] = imp;
					}
				}

				public delegate void OnGenericImplementationsChanged();
				static public OnGenericImplementationsChanged onGenericImplementationsChanged;

				/// <summary>
				/// Triggers a warning if some generic implementations weren't disabled in the template,
				/// and sends event that the available generic implementations may have changed
				/// </summary>
				public static void ListCompleted()
				{
					if (onGenericImplementationsChanged != null)
					{
						onGenericImplementationsChanged();
					}

					// check not disabled in the template
					string notDisabled = "";
					foreach (var imp in AvailableGenericImplementations)
					{
						if (imp.available)
						{
							notDisabled += imp + ", ";
						}
					}
					if (notDisabled.Length > 0)
					{
						notDisabled = notDisabled.Substring(0, notDisabled.Length - 2);
						Debug.LogWarning(ShaderGenerator2.ErrorMsg("Some Generic Implementations were not properly disabled in the template: " + notDisabled));
					}
				}

				/// <summary>
				/// Adds the Shader Property as compatible with all currently available Generic Implementations
				/// </summary>
				public static void AddCompatibleShaderProperty(ShaderProperty shaderProperty)
				{
					foreach (var imp in AvailableGenericImplementations)
					{
						if (!imp.available)
						{
							continue;
						}

						if ((imp.Compatibility & shaderProperty.Type) == shaderProperty.Type)
						{
							imp.compatibleShaderProperties.Add(shaderProperty);
						}
					}
				}

				//--------------------------------------------------------------------------------------------------------------------------------

				public VariableType VariableCompatibility { get { return Compatibility; } }
				internal override string GUILabel() { return MenuLabel; }

				[Serialization.SerializeAs("cc")] public int ChannelsCount = 1;
				[Serialization.SerializeAs("chan")] public string Channels = "X";
				[Serialization.SerializeAs("source_id")] public string sourceIdentifier;
				[Serialization.SerializeAs("needed_features")] public string NeededFeaturesStr = "";
				[Serialization.SerializeAs("custom_code_compatible")] public bool WorksWithCustomCode = false;
				public string OptionsString = "";
				[Serialization.SerializeAs("options_v")] public Dictionary<string, bool> OptionsEnabled = new Dictionary<string, bool>();
				
				string DefaultChannels = "X";

				static Dictionary<string, List<Imp_GenericFromTemplate>> AllGenericImplementations = new Dictionary<string, List<Imp_GenericFromTemplate>>();

				List<Option> options;
				struct Option
				{
					public string label;
					public string feature;
					public bool affectConfig;

					public void UpdateConfigIfNeeded(bool enabled)
					{
						if (this.affectConfig)
						{
							if (enabled)
							{
								Utils.AddIfMissing(ShaderGenerator2.CurrentConfig.ExtraTempFeatures, this.feature);
							}
							else
							{
								Utils.RemoveIfExists(ShaderGenerator2.CurrentConfig.ExtraTempFeatures, this.feature);
							}
						}
					}
				}

				// Generic Implementations that have the same identifier should have their options synchronized
				void SynchronizeOptions()
				{
					if (options == null)
					{
						return;
					}

					// the list does exist since this should be in there
					var list = AllGenericImplementations[sourceIdentifier];
					foreach (var imp in list)
					{
						if (imp != this)
						{
							SynchronizeOptions(this, imp);
						}
					}
				}

				static void SynchronizeOptions(Imp_GenericFromTemplate source, Imp_GenericFromTemplate destination)
				{
					destination.OptionsEnabled = new Dictionary<string, bool>();
					foreach (var kvp in source.OptionsEnabled)
					{
						destination.OptionsEnabled.Add(kvp.Key, kvp.Value);
					}
				}

				// Register in the static dictionary of Generic Implementations to synchronize their options
				void Register()
				{
					if (options == null)
					{
						return;
					}

					if (!AllGenericImplementations.ContainsKey(sourceIdentifier))
					{
						AllGenericImplementations.Add(sourceIdentifier, new List<Imp_GenericFromTemplate>());
					}
					else
					{
						// if there's at least one, copy its setting to sync this new instance to the other ones
						if (AllGenericImplementations[sourceIdentifier].Count > 0)
						{
							SynchronizeOptions(AllGenericImplementations[sourceIdentifier][0], this);
						}
					}

					AllGenericImplementations[sourceIdentifier].Add(this);
				}

				bool sourceIsAvailable;
				bool isTheOnlyImplementation;
				bool isNotTheLastImplementation;

				// These are determined from the template, and are not serialized in case they are updated in the template:
				public string MenuLabel;
				public string HelpMessage;
				public string WarningMessage;
				public VariableType Compatibility;
				public string VariableName;
				public string ChannelsOptions = "XYZW";

				internal override string[] NeededFeaturesExtra()
				{
					var list = new List<string>();

					if (!string.IsNullOrEmpty(NeededFeaturesStr))
					{
						list.AddRange(NeededFeaturesStr.Split(','));
					}

					if (options != null)
					{
						foreach (var option in options)
						{
							if (OptionsEnabled.ContainsKey(option.label) && OptionsEnabled[option.label])
							{
								list.Add(option.feature);
							}
						}
					}

					return list.ToArray();
				}

				public override bool HasErrors { get { return base.HasErrors || !sourceIsAvailable || isNotTheLastImplementation; } }

				public Imp_GenericFromTemplate(ShaderProperty shaderProperty) : base(shaderProperty)
				{
					onGenericImplementationsChanged += CheckSourceValidity;
					shaderProperty.onImplementationsChanged += CheckImplementationValidity;
				}

				public override void WillBeRemoved()
				{
					base.WillBeRemoved();
					onGenericImplementationsChanged -= CheckSourceValidity;
					ParentShaderProperty.onImplementationsChanged -= CheckImplementationValidity;

					if (options != null)
					{
						foreach (var option in options)
						{
							option.UpdateConfigIfNeeded(false);
						}
					}

					if (options != null)
					{
						AllGenericImplementations[sourceIdentifier].Remove(this);
					}
				}

				[Serialization.OnDeserializeCallback]
				void OnDeserialized()
				{
					// get the options from the template and not from serialization, in case options are added in the future
					var match = Imp_GenericFromTemplate.AvailableGenericImplementations.Find(gi => gi.identifier == this.sourceIdentifier);
					if (match.valid)
					{
						OptionsString = match.Options;
					}

					ParseOptions();
					Register();
					SynchronizeOptions();
					CheckSourceValidity();
				}

				void ParseOptions()
				{
					if (string.IsNullOrEmpty(OptionsString))
					{
						return;
					}

					options = new List<Option>();

					var data = Serialization.SplitExcludingBlocks(OptionsString, ',', "()");
					foreach (var d in data)
					{
						var subdata = d.Substring(1, d.Length-2).Split(',');
						var option = new Option()
						{
							label = subdata[0],
							feature = subdata[1],
							affectConfig = subdata.Length > 2 && subdata[2] == "config"
						};
						options.Add(option);

						if (!OptionsEnabled.ContainsKey(option.label))
						{
							OptionsEnabled.Add(option.label, false);
						}

						option.UpdateConfigIfNeeded(OptionsEnabled[option.label]);
					}
				}

				void CheckSourceValidity()
				{
					// check whether the source implementation is still available
					var source = AvailableGenericImplementations.Find(gi => gi.identifier == sourceIdentifier);

					sourceIsAvailable = source.valid;

					if (source.valid)
					{
						this.MenuLabel = source.MenuLabel;
						this.HelpMessage = source.HelpMessage;
						this.WarningMessage = source.WarningMessage;
						this.ChannelsOptions = source.ChannelsOptions;
						this.Compatibility = source.Compatibility;
						this.VariableName = source.VariableName;

						this.DeduceChannelsSettings(ParentShaderProperty);
					}
				}

				void DeduceChannelsSettings(ShaderProperty shaderProperty)
				{
					// deduce the channels/count based on the shader property
					switch (shaderProperty.Type)
					{
						case VariableType.@float: this.ChannelsCount = 1; break;
						case VariableType.float2: this.ChannelsCount = 2; break;
						case VariableType.float3:
						case VariableType.color: this.ChannelsCount = 3; break;
						case VariableType.float4:
						case VariableType.color_rgba: this.ChannelsCount = 4; break;
					}

					string defaultChannels = "";
					for (int i = 0; i < this.ChannelsCount; i++)
					{
						defaultChannels += this.ChannelsOptions[i % this.ChannelsOptions.Length];
					}
					this.DefaultChannels = defaultChannels;

					// set Channels, or preserve existing ones as far as possible
					var prevChannels = Channels;
					Channels = "";
					for (int i = 0; i < ChannelsCount; i++)
					{
						if (prevChannels != null && i < prevChannels.Length && this.ChannelsOptions.Contains(prevChannels[i].ToString()))
							Channels += prevChannels[i];
						else
							Channels += this.ChannelsOptions[i % this.ChannelsOptions.Length];
					}
				}

				/// <summary>
				/// Verifies that this generic implementation isn't the only one, and is at the end of the implementations list
				/// </summary>
				void CheckImplementationValidity()
				{
					isTheOnlyImplementation = ParentShaderProperty.implementations.Count == 1;

					// iterate through the implementations, and see if any implementation past this one is not a Generic one
					isNotTheLastImplementation = false;
					bool reachedThis = false;
					foreach (var imp in ParentShaderProperty.implementations)
					{
						if (imp == this)
						{
							reachedThis = true;
						}

						if (!reachedThis)
						{
							continue;
						}

						if (!(imp is Imp_GenericFromTemplate))
						{
							isNotTheLastImplementation = true;
							break;
						}
					}
				}

				internal override string PrintVariableFragment(string inputSource, string outputSource, string arguments)
				{
					// Don't sample at shader property declaration, but at shader property usage,
					// except if there's no other implementations: use a 1 constant that will be multiplied

					if (isTheOnlyImplementation)
					{
						switch (ParentShaderProperty.Type)
						{
							case VariableType.@float: return "1";
							case VariableType.float2: return "float2(1,1)";
							case VariableType.color:
							case VariableType.float3: return "float3(1,1,1)";
							case VariableType.color_rgba:
							case VariableType.float4: return "float4(1,1,1,1)";
						}
					}

					return null;
				}

				public string Print()
				{
					string op = isTheOnlyImplementation ? " * " : PrintOperator();
					return string.Format("{0}{1}.{2}", op, VariableName, Channels.ToLowerInvariant());
				}

				public string PrintCustomCode()
				{
					return VariableName;
				}

				internal override void NewLineGUI(bool usedByCustomCode)
				{
					BeginHorizontal();
					ShaderGenerator2.ContextualHelpBox("Special implementation defined in the template" + (HelpMessage != null ? ":\n" + HelpMessage : "."));
					EndHorizontal();

					if (!string.IsNullOrEmpty(WarningMessage))
					{
						BeginHorizontal();
						EditorGUILayout.HelpBox(WarningMessage, MessageType.Warning);
						EndHorizontal();
					}

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? Channels != DefaultChannels : Channels != GetDefaultImplementation<Imp_GenericFromTemplate>().Channels;
						SGUILayout.InlineLabel("Swizzle", highlighted);

						if (usedByCustomCode)
						{
							using (new EditorGUI.DisabledScope(true))
							{
								GUILayout.Label(TCP2_GUI.TempContent("Defined in Custom Code"), SGUILayout.Styles.ShurikenValue, GUILayout.Height(16), GUILayout.ExpandWidth(false));
							}
						}
						else
						{
							if (ChannelsCount == 1)
							{
								Channels = SGUILayout.GenericSelector(ChannelsOptions, Channels);
							}
							else
							{
								Channels = SGUILayout.GenericSwizzle(Channels, ChannelsCount, ChannelsOptions);
							}
						}
					}
					EndHorizontal();

					if (options != null)
					{
						for (int i = 0; i < options.Count; i++)
						{
							if (!OptionsEnabled.ContainsKey(options[i].label))
							{
								OptionsEnabled.Add(options[i].label, false);
							}

							BeginHorizontal();
							{
								EditorGUI.BeginChangeCheck();
								bool highlighted = !IsDefaultImplementation ? OptionsEnabled[options[i].label] : OptionsEnabled[options[i].label] != GetDefaultImplementation<Imp_GenericFromTemplate>().OptionsEnabled[options[i].label];
								SGUILayout.InlineLabel(options[i].label, highlighted);
								OptionsEnabled[options[i].label] = SGUILayout.Toggle(OptionsEnabled[options[i].label]);
								if (EditorGUI.EndChangeCheck())
								{
									options[i].UpdateConfigIfNeeded(OptionsEnabled[options[i].label]);
									SynchronizeOptions();
								}
							}
							EndHorizontal();
						}

						BeginHorizontal();
						{
							TCP2_GUI.HelpBoxLayout("The options for this Special Implementation are global across all the Properties.", MessageType.Info);
						}
						EndHorizontal();
					}

					// errors
					if (!sourceIsAvailable)
					{
						GUILayout.Space(4);
						BeginHorizontal();
						{
							TCP2_GUI.HelpBoxLayout("This implementation is not available anymore, based on the selected features and options.", MessageType.Error);
						}
						EndHorizontal();
					}

					if (isNotTheLastImplementation)
					{
						GUILayout.Space(4);
						BeginHorizontal();
						{
							TCP2_GUI.HelpBoxLayout("This special implementation depends on the shader context and has to be the last implementation in the list.\nPlease drag its handle on the left and move it last.", MessageType.Error);
						}
						EndHorizontal();
					}
				}
			}

			[Serialization.SerializeAs("imp_enum")]
			public class Imp_Enum : Implementation
			{
				public static VariableType VariableCompatibility { get { return VariableType.fixed_function_enum; } }
				public static string MenuLabel { get { return "Enum (Fixed Function)"; } }
				internal override string GUILabel() { return MenuLabel; }

				[Serialization.SerializeAs("value_type")] public int ValueType;
				[Serialization.SerializeAs("value")] public int EnumValue;
				[Serialization.SerializeAs("enum_type")] public string EnumType;

				Enums.OrderedEnum[] enumValues;
				string[] enumDisplayNames;

				string[] options = new string[]
				{
					"Constant",
					"Material Property"
				};

				public void SetValueTypeFromString(string valueTypeStr)
				{
					int index = Array.IndexOf(options, valueTypeStr);
					ValueType = index;
				}

				public Imp_Enum(ShaderProperty shaderProperty) : base(shaderProperty)
				{
				}

				[Serialization.OnDeserializeCallback]
				public void SetEnumType()
				{
					var type = typeof(GameObject).Assembly.GetType(EnumType, false);

					if (type == null)
					{
						var assemblies = AppDomain.CurrentDomain.GetAssemblies();
						foreach (var assembly in assemblies)
						{
							type = assembly.GetType(EnumType);
							if (type != null)
							{
								break;
							}
						}
					}

					if (type == null)
					{
						throw new ArgumentException("Can't find Enum Type: " + EnumType);
					}

					if (!type.IsEnum)
					{
						throw new ArgumentException("Found Type is not an Enum: " + EnumType);
					}

					enumValues = Enums.GetOrderedEnumValues(type);
					enumDisplayNames = Array.ConvertAll(enumValues, ev => ev.displayName);
				}

				public void Parse(string strValue)
				{
					int index = Array.FindIndex(enumValues, ev => ev.value.ToString() == strValue);
					if (index < 0)
					{
						Debug.LogError(ShaderGenerator2.ErrorMsg(string.Format("Can't parse value '{0}' for type '{1}'.", strValue, EnumType)));
						return;
					}

					EnumValue = index;
				}

				bool IsConstant()
				{
					return ValueType == 0;
				}

				string PropertyName()
				{
					return string.Format("_{0}", ToLowerCamelCase(ParentShaderProperty.Name));
				}

				internal override string PrintVariableFixedFunction()
				{
					if (IsConstant())
					{
						return enumValues[EnumValue].value.ToString();
					}
					else
					{
						return string.Format("[{0}]", PropertyName());
					}
				}

				internal override string PrintProperty(string indent)
				{
					if (!IsConstant())
					{
						return base.PrintProperty(indent) + string.Format("[Enum({0})] {1} (\"{2}\", Float) = {3}", EnumType.Replace("+", "."), PropertyName(), Label, Convert.ChangeType(enumValues[EnumValue].value, TypeCode.Int32));
					}
					else
					{
						return null;
					}
				}

				/*
				internal override string PrintVariableDeclare(string indent) { return string.Format("float {0};", PropertyName); }
				internal override string PrintVariableFragment(string inputSource, string outputSource, string arguments) { return PropertyName; }
				*/

				internal override void NewLineGUI(bool usedByCustomCode)
				{
					base.NewLineGUI(usedByCustomCode);

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? ValueType > 0 : ValueType != GetDefaultImplementation<Imp_Enum>().ValueType;
						SGUILayout.InlineLabel(TCP2_GUI.TempContent("Type"), highlighted);
						ValueType = SGUILayout.Popup(ValueType, options);
					}
					EndHorizontal();

					if (enumValues == null)
					{
						BeginHorizontal();
						{
							SGUILayout.InlineLabel(TCP2_GUI.TempContent("Couldn't find enum type: '" + EnumType + "'"));
						}
						EndHorizontal();
					}
					else
					{
						BeginHorizontal();
						{
							bool highlighted = !IsDefaultImplementation ? EnumValue > 0 : EnumValue != GetDefaultImplementation<Imp_Enum>().EnumValue;
							SGUILayout.InlineLabel(TCP2_GUI.TempContent(IsConstant() ? "Value" : "Default Value"), highlighted);
							EnumValue = SGUILayout.Popup(EnumValue, enumDisplayNames);
						}
						EndHorizontal();
					}
				}

				// Used to show the properties in the Features tab directly
				internal void EmbeddedGUI(float indent = 0, float labelWidth = 130)
				{
					// Embedded through the "mult_fs" UIFeature
					/*
					GUILayout.BeginHorizontal();
					{
						GUILayout.Space(indent);
						TCP2_GUI.SubHeader("Type", null, true, labelWidth);
						ValueType = EditorGUILayout.Popup(ValueType, options);
					}
					GUILayout.EndHorizontal();
					*/

					GUILayout.BeginHorizontal();
					{
						GUILayout.Space(indent);
						bool highlighted = !IsDefaultImplementation || EnumValue != GetDefaultImplementation<Imp_Enum>().EnumValue;
						TCP2_GUI.SubHeader(IsConstant() ? "Value" : "Default Value", null, highlighted, labelWidth + 4);
						GUILayout.Space(-4); // hack to align the highlighted part with the regular UIFeatures
						EnumValue = EditorGUILayout.Popup(EnumValue, enumDisplayNames);
					}
					GUILayout.EndHorizontal();
				}
			}

			[Serialization.SerializeAs("imp_customcode")]
			public class Imp_CustomCode : Implementation
			{
				public static VariableType VariableCompatibility { get { return VariableTypeAll; } }
				public static string MenuLabel { get { return "Special/Custom Code"; } }
				internal override string GUILabel() { return MenuLabel; }
				internal override bool HasOperator() { return false; }

				public enum PrependType
				{
					Disabled,
					Embedded,
					ExternalFile
				}

				[Serialization.SerializeAs("prepend_type")] public PrependType prependType = PrependType.Disabled;
				[Serialization.SerializeAs("prepend_code")] public string prependCode = "";
				[Serialization.SerializeAs("prepend_file")] public string prependFileGuid = "";
				[Serialization.SerializeAs("prepend_file_block")] public string prependFileBlock = "";
				[Serialization.SerializeAs("preprend_params")] public Dictionary<string, string> prependParametersValues = new Dictionary<string, string>(); // values for the parameters of the defined block in the prepend file

				TextAsset prependFile;
				bool prependFileBlockFound;
				string[] prependBlocks;

				struct PrependReference
				{
					public ShaderProperty.VariableType variableType;
					public string variableName;
					public string defaultValueOrComment;
					public bool isComment;

					public PrependReference(ShaderProperty.VariableType type, string name, string value, bool comment)
					{
						variableType = type;
						variableName = name;
						defaultValueOrComment = value;
						isComment = comment;

						label = string.Format("{0} ({1})", name, type);
					}

					public string label { get; private set; }
				}
				List<PrependReference> prependReferences;
				List<string> prependLines;

				[Serialization.SerializeAs("code")] public string code = "";
				public bool usesReplacementTags = false;
				Dictionary<string, List<string>> replacementParts = new Dictionary<string, List<string>>();
				List<int> usedImplementations = new List<int>();
				public string tagError = null;

				public override bool HasErrors { get { return base.HasErrors | !string.IsNullOrEmpty(tagError) | (prependType == PrependType.ExternalFile && prependFile != null && !prependFileBlockFound); } }

				public Imp_CustomCode(ShaderProperty shaderProperty) : base(shaderProperty)
				{
					ParentShaderProperty.onImplementationsChanged += onImplementationsChanged;
					ShaderGenerator2.onProjectChange += onProjectChanged;
				}

				public override void WillBeRemoved()
				{
					ParentShaderProperty.onImplementationsChanged -= onImplementationsChanged;
					ShaderGenerator2.onProjectChange -= onProjectChanged;
				}

				void onImplementationsChanged()
				{
					CheckReplacementTags();
				}

				void onProjectChanged()
				{
					TryToFindPrependCodeBlock();
					CheckReplacementTags();
				}

				[Serialization.OnDeserializeCallback]
				void OnDeserialized()
				{
					TryFindPrependFileFromGuid();
					CheckReplacementTags();
				}

				public override void OnPasted()
				{
					TryFindPrependFileFromGuid();
					CheckReplacementTags();
				}
				
				void TryFindPrependFileFromGuid()
				{
					if (!string.IsNullOrEmpty(prependFileGuid))
					{
						var file = AssetDatabase.LoadAssetAtPath<TextAsset>(AssetDatabase.GUIDToAssetPath(prependFileGuid));
						if (file != null)
						{
							prependFile = file;
							TryToFindPrependCodeBlock();
						}
						else
						{
							prependFileGuid = "";
							prependFile = null;
						}
					}
				}

				Dictionary<string, string> shaderUniqueVariableNamesMapping;

				void PrintPrependCodeIfNeeded()
				{
					PrintPrependCodeIfNeeded(null, null, null, null, ParentShaderProperty.Program);
				}
				Dictionary<string, string> PrintPrependCodeIfNeeded(Dictionary<Implementation, string> cachedVariables, string inputSource, string outputSource, string arguments, ProgramType program)
				{
					if (prependType == PrependType.Disabled)
					{
						return null;
					}

					if (prependType == PrependType.Embedded && !string.IsNullOrEmpty(prependCode))
					{
						string pCode = prependCode;
						if (replacementParts.ContainsKey("prependCode"))
						{
							var list = replacementParts["prependCode"];
							pCode = ParseReplacementParts(list, cachedVariables, inputSource, outputSource, arguments, program);
						}

						var lines = pCode.Split(new string[] { "\r\n", "\n" }, System.StringSplitOptions.None);
						foreach (var l in lines)
						{
							ShaderGenerator2.AppendLineBefore(l);
						}
					}
					else if (prependType == PrependType.ExternalFile && prependFile != null && prependFileBlockFound)
					{
						if (shaderUniqueVariableNamesMapping == null && prependReferences != null)
						{
							shaderUniqueVariableNamesMapping = new Dictionary<string, string>();
							foreach (var reference in prependReferences)
							{
								if (reference.isComment) continue;
								shaderUniqueVariableNamesMapping.Add(reference.variableName, string.Format("{0}_{1}", reference.variableName, ShaderGenerator2.GlobalUniqueId));
							}
						}

						string header = string.Format("// {0} : {1}", prependFile.name, prependFileBlock);
						string commentLine = "//" + new string('-', header.Length - 2);
						ShaderGenerator2.AppendLineBefore(commentLine);
						ShaderGenerator2.AppendLineBefore(header);

						// generate declaration of each parameter with its value
						for (int i = 0; i < prependReferences.Count; i++)
						{
							var reference = prependReferences[i];
							if (reference.isComment)
							{
								continue;
							}

							var value = prependParametersValues[reference.variableName];

							if (replacementParts.ContainsKey(reference.variableName))
							{
								var list = replacementParts[reference.variableName];
								value = ParseReplacementParts(list, cachedVariables, inputSource, outputSource, arguments, program);
							}

							ShaderGenerator2.AppendLineBefore(string.Format("{0} {1} = {2};", 
								ShaderProperty.VariableTypeToShaderCode(reference.variableType),
								shaderUniqueVariableNamesMapping[reference.variableName],
								value));
						}

						// process and append the block lines
						var variableRegex = new Regex(@"[^\w](_(\w+)_)[^\w]+?", RegexOptions.ECMAScript);
						Dictionary<string, string> uniqueVariableReplacements = new Dictionary<string, string>();
						foreach (var l in prependLines)
						{
							string line = l;

							// replace the variable names with their unique id counterpart to avoid duplicate declarations
							foreach (var reference in prependReferences)
							{
								if (reference.isComment) continue;
								line = line.Replace(reference.variableName, shaderUniqueVariableNamesMapping[reference.variableName]);
							}

							// find and replace variables with the _name_ format, to avoid duplicate declarations
							var matches = variableRegex.Matches(line);
							foreach (Match match in matches)
							{
								string toReplace = match.Groups[1].Value;
								if (!uniqueVariableReplacements.ContainsKey(toReplace))
								{
									uniqueVariableReplacements.Add(toReplace, match.Groups[2].Value + "_" + ShaderGenerator2.GlobalUniqueId);
								}

								line = line.Replace(toReplace, uniqueVariableReplacements[toReplace]);
							}

							ShaderGenerator2.AppendLineBefore(line);
						}
						ShaderGenerator2.AppendLineBefore(commentLine);

						return uniqueVariableReplacements;
					}

					return null;
				}

				internal override string PrintVariableFragment(string inputSource, string outputSource, string arguments)
				{
					PrintPrependCodeIfNeeded();
					return code.Length > 0 && !char.IsWhiteSpace(code[0]) ? " " + code : code;
				}

				//called if the custom code (or prepend code) use {n} tags, to directly use implementations within the custom code
				public string PrintVariableReplacement(ref HashSet<Implementation> usedImplementations, string inputSource, string outputSource, string arguments, ProgramType program)
				{
					if (!string.IsNullOrEmpty(tagError))
					{
						// This shouldn't happen because this error is checked beforehand (disables 'Generate' button)
						Debug.LogError(ShaderGenerator2.ErrorMsg("Custom Code error: " + tagError));
						return null;
					}

					if (replacementParts.Count == 0)
					{
						// This shouldn't happen because this error is checked beforehand (disables 'Generate' button)
						Debug.LogError(ShaderGenerator2.ErrorMsg("Custom Code error: 'replacementParts' is null or empty"));
						return null;
					}

					int customCodeIndex = ParentShaderProperty.implementations.IndexOf(this);
					string output = "";

					// First pass: see which implementations are sampled and how many times (to possibly cache them)
					var usedImpsMultipleTimes = new List<Implementation>();
					foreach (var partsList in replacementParts.Values)
					{
						foreach (var part in partsList)
						{
							if (part.StartsWith("tag:"))
							{
								var intStr = part.Substring("tag:".Length);
								int impIndex = int.Parse(intStr) - 1;

								if (impIndex == customCodeIndex)
								{
									// This shouldn't happen because this error is checked beforehand (disables 'Generate' button)
									Debug.LogError(ShaderGenerator2.ErrorMsg("Custom Code error: the Custom Code implementation cannot reference itself!\nCustom Code index = " + customCodeIndex + ", Reference = {" + impIndex + "}"));
									return null;
								}

								if (impIndex < customCodeIndex)
								{
									// This shouldn't happen because this error is checked beforehand (disables 'Generate' button)
									Debug.LogError(ShaderGenerator2.ErrorMsg("Custom Code error: the Custom Code implementation cannot reference previous implementations!\nCustom Code index = " + customCodeIndex + ", Reference = {" + impIndex + "}"));
									return null;
								}

								var imp = ParentShaderProperty.implementations[impIndex];

								if (usedImplementations.Contains(imp) && !usedImpsMultipleTimes.Contains(imp))
								{
									usedImpsMultipleTimes.Add(imp);
								}

								usedImplementations.Add(imp);
							}
						}
					}

					// Sample the implementations that are used multiple times beforehand
					var cachedVariables = new Dictionary<Implementation, string>();
					if (usedImpsMultipleTimes.Count > 0)
					{
						ShaderGenerator2.AppendLineBefore("// Sampled in Custom Code");
						for (int i = 0; i < usedImpsMultipleTimes.Count; i++)
						{
							var imp = usedImpsMultipleTimes[i];

							// unique variable name based on the implementation
							string variableName = string.Format("imp_{0}", ShaderGenerator2.GlobalUniqueId);
							cachedVariables.Add(imp, variableName);

							string variableType = "float";
							var compatibility = (VariableType)imp.GetType().GetProperty("VariableCompatibility", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).GetValue(null, null);
							if (CheckVariableType(compatibility, VariableType.float4)
								|| CheckVariableType(compatibility, VariableType.color_rgba))
							{
								variableType = "float4";
							}
							else if (CheckVariableType(compatibility, VariableType.float3)
								|| CheckVariableType(compatibility, VariableType.color))
							{
								variableType = "float3";
							}
							else if (CheckVariableType(compatibility, VariableType.float2))
							{
								variableType = "float2";
							}
							string format = string.Format("{0} {1} = {{0}};", variableType, variableName);

							string argumentsHideChannels = AddArgument("hide_channels", "true", arguments);

							// special case: when using deferred sampling, allow referencing special implementations because everything is sampled at the same time
							if (ParentShaderProperty.deferredSampling && imp is Imp_GenericFromTemplate)
							{
								ShaderGenerator2.AppendLineBefore(string.Format(format, (imp as Imp_GenericFromTemplate).PrintCustomCode()));
							}
							else if (program == ProgramType.Vertex)
							{
								ShaderGenerator2.AppendLineBefore(string.Format(format, imp.PrintVariableVertex(inputSource, outputSource, argumentsHideChannels)));
							}
							else
							{
								ShaderGenerator2.AppendLineBefore(string.Format(format, imp.PrintVariableFragment(inputSource, outputSource, argumentsHideChannels)));
							}
						}
					}

					// Prepend code if any
					var replacementDict = PrintPrependCodeIfNeeded(cachedVariables, inputSource, outputSource, arguments, program);

					// Print the custom code with cached variables
					arguments = AddArgument("hide_channels", "true", arguments);
					if (replacementParts.ContainsKey("code"))
					{
						var list = replacementParts["code"];
						output += ParseReplacementParts(list, cachedVariables, inputSource, outputSource, arguments, program);
					}

					// Replace unique variables (format _name_) from the external file, if any
					if (replacementDict != null)
					{
						foreach (var kvp in replacementDict)
						{
							output = output.Replace(kvp.Key, kvp.Value);
						}
					}

					if (output.Length > 0 && !char.IsWhiteSpace(output[0]))
					{
						output = " " + output;
					}

					return output;
				}

				string ParseReplacementParts(List<string> replacementPartsList, Dictionary<Implementation, string> cachedVariables, string inputSource, string outputSource, string arguments, ProgramType program)
				{
					string output = "";
					foreach (var part in replacementPartsList)
					{
						if (part.StartsWith("tag:"))
						{
							var intStr = part.Substring("tag:".Length);
							int impIndex = int.Parse(intStr) - 1;
							var imp = ParentShaderProperty.implementations[impIndex];

							if (cachedVariables.ContainsKey(imp))
							{
								output += cachedVariables[imp];
							}
							else
							{
								// special case: when using deferred sampling, allow referencing special implementations because everything is sampled at the same time
								if (imp is Imp_GenericFromTemplate)
								{
									output += (imp as Imp_GenericFromTemplate).PrintCustomCode();
								}
								else if (program == ProgramType.Vertex)
								{
									output += imp.PrintVariableVertex(inputSource, outputSource, arguments);
								}
								else
								{
									output += imp.PrintVariableFragment(inputSource, outputSource, arguments);
								}
							}
						}
						else
						{
							output += part;
						}
					}
					return output;
				}

				internal override void NewLineGUI(bool usedByCustomCode)
				{
					BeginHorizontal();
					ShaderGenerator2.ContextualHelpBox("Insert arbitrary custom shader code.");
					EndHorizontal();

					// Prepend system
					BeginHorizontal();
					{
						SGUILayout.InlineLabel("Prepend Type");
						EditorGUI.BeginChangeCheck();
						prependType = (PrependType)SGUILayout.EnumPopup(prependType);
						if (EditorGUI.EndChangeCheck())
						{
							CheckReplacementTags();
							TryToFindPrependCodeBlock();
						}
					}
					EndHorizontal();

					if (prependType == PrependType.Embedded)
					{
						BeginHorizontal();
						{
							SGUILayout.InlineLabel("Prepend Code");
							EditorGUI.BeginChangeCheck();
							prependCode = SGUILayout.TextArea(prependCode, 90, true);
							if (EditorGUI.EndChangeCheck())
							{
								CheckReplacementTags();
							}
						}
						EndHorizontal();
						GUILayout.Space(3);
					}
					else if (prependType == PrependType.ExternalFile)
					{
						BeginHorizontal();
						{
							SGUILayout.InlineLabel("Prepend File");
							EditorGUI.BeginChangeCheck();
							prependFile = SGUILayout.ObjectField<TextAsset>(prependFile);
							if (EditorGUI.EndChangeCheck())
							{
								prependFileGuid = prependFile != null ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(prependFile)) : "";
								TryToFindPrependCodeBlock();
							}
						}
						EndHorizontal();
						GUILayout.Space(4);

						if (prependBlocks == null || prependBlocks.Length == 0)
						{
							BeginHorizontal();
							{
								EditorGUILayout.HelpBox("Please select a valid prepend file.", MessageType.Info);
							}
							EndHorizontal();
						}
						else
						{
							BeginHorizontal();
							{
								SGUILayout.InlineLabel("Block Name");
								EditorGUI.BeginChangeCheck();

								//prependFileBlock = SGUILayout.TextField(prependFileBlock, true);

								int index = -1;
								index = Array.IndexOf(prependBlocks, prependFileBlock);
								index = SGUILayout.Popup(index, prependBlocks);

								if (EditorGUI.EndChangeCheck())
								{
									prependFileBlock = prependBlocks[index];
									TryToFindPrependCodeBlock();
								}
							}
							EndHorizontal();

							if (prependFileBlockFound)
							{
								for (int i = 0; i < prependReferences.Count; i++)
								{
									var reference = prependReferences[i];

									if (reference.isComment)
									{
										BeginHorizontal(12);
										{
											EditorGUILayout.HelpBox(reference.defaultValueOrComment, MessageType.None);
										}
										EndHorizontal();
									}
									else
									{
										BeginHorizontal(12);
										{
											SGUILayout.InlineLabel(reference.label);

											EditorGUI.BeginChangeCheck();
											prependParametersValues[reference.variableName] = SGUILayout.TextField(prependParametersValues[reference.variableName], false);
											if (EditorGUI.EndChangeCheck())
											{
												CheckReplacementTags();
											}
										}
										EndHorizontal();
									}
								}
							}

							if (!prependFileBlockFound)
							{
								BeginHorizontal();
								{
									EditorGUILayout.HelpBox("Could not find the specified code block in the linked prepend file.", MessageType.Error);
								}
								EndHorizontal();
							}
						}

						GUILayout.Space(8f);
					}

					BeginHorizontal();
					{
						SGUILayout.InlineLabel("Code");
						EditorGUI.BeginChangeCheck();
						code = SGUILayout.TextField(code, monospace: true);
						if (EditorGUI.EndChangeCheck())
						{
							CheckReplacementTags();
						}
					}
					EndHorizontal();

					if (tagError != null)
					{
						BeginHorizontal();
						{
							GUILayout.Space(4);
							TCP2_GUI.HelpBoxLayout(tagError, MessageType.Error);
						}
						EndHorizontal();
					}
					else
					{
						BeginHorizontal();
						{
							GUILayout.Space(4);
							TCP2_GUI.HelpBoxLayout("You can reference other implementations using <b>{n}</b> notation where <b>n</b> is the index of another implementation for this property, e.g.:\n<i>dot({1}, {2})</i>\n\nNote: the <b>operator</b> and <b>channels</b> for referenced implementations will be ignored!", MessageType.Info);
						}
						EndHorizontal();
					}
				}

				public void TryToFindPrependCodeBlock()
				{
					if (prependType != PrependType.ExternalFile || string.IsNullOrEmpty(this.prependFileGuid))
					{
						return;
					}

					prependFileBlockFound = false;
					string[] lines = System.IO.File.ReadAllLines(Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length) + AssetDatabase.GetAssetPath(prependFile));


					// find all prepend blocks (not the best place to do it though)
					var blockList = new List<string>();
					for (int i = 0; i < lines.Length; i++)
					{
						var line = lines[i].Trim();
						if (line.StartsWith("//#") && line.EndsWith(":"))
						{
							blockList.Add(line.Substring("//#".Length, line.Length - "//#:".Length).Trim());
						}
					}
					prependBlocks = blockList.ToArray();

					// find matching prepend block
					for (int i = 0; i < lines.Length; i++)
					{
						var line = lines[i];
						if (line.StartsWith("///"))
						{
							continue;
						}

						if (line.StartsWith("//#"))
						{
							if (prependFileBlockFound)
							{
								if (line.Contains(":"))
								{
									// marks the end of the current block
									break;
								}
							}
							else
							{
								// found matching block
								var trimmed = line.Substring("//#".Length).Trim().TrimEnd(':');
								if (trimmed == prependFileBlock)
								{
									prependFileBlockFound = true;
									ParsePrependBlock(ref lines, i + 1);
									return;
								}
							}
						}
					}
				}

				void ParsePrependBlock(ref string[] lines, int startIndex)
				{
					prependReferences = new List<PrependReference>();
					prependLines = new List<string>();

					for (int i = startIndex; i < lines.Length; i++)
					{
						string line = lines[i];

						// end of block
						if (line.StartsWith("//#"))
						{
							// prepend comment
							if (line.StartsWith("//# !"))
							{
								string comment = line.Substring("//# !".Length).Trim();
								prependReferences.Add(new PrependReference(VariableType.@float, "comment", comment, true));
							}
							// new block = end of this block
							else if (line.Contains(":"))
							{
								break;
							}
							// prepend reference
							else
							{
								// inputs description in the form:
								// '//# type variableName [defaultValue]'
								// will be translated into an UI where user can type the value they want, including {n} notation

								string prependRefStr = line.Substring("//#".Length).Trim();
								string[] parts = prependRefStr.Split(new char[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
								if (parts.Length < 2)
								{
									Debug.LogError(ShaderGenerator2.ErrorMsg("Invalid prepend code reference, it should be in the following format:\n\"//# type name [defaultValue]\" (e.g. \"//# float4 myVariable (1.1, 2.0, 0.0, 4.0)\")\nParsed line:\n" + line));
								}
								else
								{
									var vType = (ShaderProperty.VariableType)System.Enum.Parse(typeof(ShaderProperty.VariableType), parts[0]);
									if (!System.Enum.IsDefined(typeof(ShaderProperty.VariableType), vType))
									{
										Debug.LogError(ShaderGenerator2.ErrorMsg("Invalid variable type defined for prepend code reference:\n" + line));
									}
									else
									{
										string name = parts[1];
										string defaultValue = parts.Length > 2 ? parts[2] : "";
										prependReferences.Add(new PrependReference(vType, name, defaultValue, false));
									}
								}
							}
						}
						else if (line.StartsWith("///"))
						{
							// ignore comments for the prepend file only
							continue;
						}
						else
						{
							// add the line to the ones to be printed
							prependLines.Add(line);
						}
					}

					// trim all empty lines at the end of the list
					for (int i = prependLines.Count-1; i >= 0; i--)
					{
						if (!string.IsNullOrEmpty(prependLines[i]))
						{
							break;
						}
						prependLines.RemoveAt(i);
					}

					// Initialize the prepend code references
					if (prependReferences.Count > 0)
					{
						// new list
						if (prependParametersValues == null)
						{
							prependParametersValues = new Dictionary<string, string>();
						}

						foreach (var reference in prependReferences)
						{
							if (reference.isComment) continue;
							if (!prependParametersValues.ContainsKey(reference.variableName))
							{
								string defaultValue = string.IsNullOrEmpty(reference.defaultValueOrComment) ? getDefaultValueForType(reference.variableType) : reference.defaultValueOrComment;
								prependParametersValues.Add(reference.variableName, defaultValue);
							}
						}

						List<string> keysToRemove = new List<string>();
						foreach (var kvp in prependParametersValues)
						{
							if (!prependReferences.Exists(reference => reference.variableName == kvp.Key))
							{
								keysToRemove.Add(kvp.Key);
							}
						}
						foreach (var key in keysToRemove)
						{
							prependParametersValues.Remove(key);
						}
					}
				}

				string getDefaultValueForType(ShaderProperty.VariableType variableType)
				{
					switch(variableType)
					{
						case VariableType.@float:		return "0.0";
						case VariableType.float2:		return "float2(0.0, 0.0)";
						case VariableType.float3:
						case VariableType.color: return "float3(0.0, 0.0, 0.0)";
						case VariableType.float4:
						case VariableType.color_rgba: return "float4(0.0, 0.0, 0.0, 0.0)";
						default: return "";
					}
				}

				string[] LoadPrependBlock()
				{
					bool inBlock = false;
					var prepandBlockLines = new List<string>();
					string[] lines = System.IO.File.ReadAllLines(Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length) + AssetDatabase.GetAssetPath(prependFile));
					for (int i = 0; i < lines.Length; i++)
					{
						var line = lines[i];

						if (line.StartsWith("//#"))
						{
							if (inBlock)
							{
								if (line.Contains(":"))
								{
									// marks the end of the current block
									break;
								}
								else
								{
									// inputs description in the form:
									// "type variableName"
									// will be translated into an UI where user can link an implementation to each input
									// TODO
								}
							}
							else
							{
								var trimmed = line.Substring("//#".Length).Trim().TrimEnd(':');
								if (trimmed == prependFileBlock)
								{
									inBlock = true;
								}
							}
						}
						else if (inBlock)
						{
							prepandBlockLines.Add(line);
						}
					}

					// trim all empty lines at the end of the list
					for (int i = prepandBlockLines.Count-1; i >= 0; i--)
					{
						if (!string.IsNullOrEmpty(prepandBlockLines[i]))
						{
							break;
						}
						prepandBlockLines.RemoveAt(i);
					}

					return prepandBlockLines.ToArray();
				}

				public void CheckReplacementTags()
				{
					if (usedImplementations != null)
					{
						foreach (int value in usedImplementations)
						{
							ParentShaderProperty.usedImplementationsForCustomCode.Remove(value);
						}
					}

					usesReplacementTags = false;
					replacementParts.Clear();
					usedImplementations.Clear();
					tagError = null;
					int customCodeIndex = ParentShaderProperty.implementations == null ? -1 : ParentShaderProperty.implementations.IndexOf(this);
					int maxIndex = ParentShaderProperty.implementations == null ? 0 : ParentShaderProperty.implementations.Count - 1;

					// parse code
					var codeReplacements = ReplaceNNotationWithReplacementTags(code, customCodeIndex, maxIndex);
					if (codeReplacements != null)
					{
						replacementParts.Add("code", new List<string>());
						replacementParts["code"].AddRange(codeReplacements.Value.parts);
						usedImplementations.AddRange(codeReplacements.Value.usedImplementations);
					}

					// parse prepend code (embedded mode)
					if (prependType == PrependType.Embedded)
					{
						var cr = ReplaceNNotationWithReplacementTags(prependCode, customCodeIndex, maxIndex);
						if (cr != null)
						{
							replacementParts.Add("prependCode", new List<string>());
							replacementParts["prependCode"].AddRange(cr.Value.parts);
							usedImplementations.AddRange(cr.Value.usedImplementations);
						}
					}
					// parse prepend code (external file mode)
					else if (prependType == PrependType.ExternalFile && prependReferences != null && prependParametersValues != null)
					{
						for (int i = 0; i < prependReferences.Count; i++)
						{
							var reference = prependReferences[i];

							if (reference.isComment)
							{
								continue;
							}

							string value = prependParametersValues[reference.variableName];
							if (value.Contains("{"))
							{
								var cr = ReplaceNNotationWithReplacementTags(value, customCodeIndex, maxIndex);
								if (cr != null)
								{
									string key = prependReferences[i].variableName;
									replacementParts.Add(key, new List<string>());
									replacementParts[key].AddRange(cr.Value.parts);
									usedImplementations.AddRange(cr.Value.usedImplementations);
								}
							}
						}
					}

					usedImplementations = usedImplementations.Distinct().ToList();
					ParentShaderProperty.usedImplementationsForCustomCode.AddRange(usedImplementations);
				}

				struct ReplacementTagsResult
				{
					public List<string> parts;
					public List<int> usedImplementations;
				}

				ReplacementTagsResult? ReplaceNNotationWithReplacementTags(string input, int customCodeIndex, int maxIndex)
				{
					//explore the string and find all '{n}' where n = number, and construct list of parts
					bool tag = false;
					string currentPart = null;
					var parts = new List<string>();
					var usedImps = new List<int>();
					for (int i = 0; i < input.Length; i++)
					{
						char c = input[i];

						//inside tag (maybe)
						if (tag)
						{
							//closing tag
							if (c == '}')
							{
								if (string.IsNullOrEmpty(currentPart))
								{
									tagError = "Invalid code: empty replacement tag";
									return null;
								}

								tag = false;
								parts.Add("tag:" + currentPart);

								int usedImpIndex;
								if (int.TryParse(currentPart, out usedImpIndex))
								{
									usedImpIndex -= 1;
									if (!usedImps.Contains(usedImpIndex))
									{
										usedImps.Add(usedImpIndex);

										if (usedImpIndex == customCodeIndex)
										{
											tagError = "Invalid code: the Custom Code implementation cannot reference itself";
											return null;
										}
										else if (usedImpIndex < customCodeIndex)
										{
											tagError = "Invalid code: the Custom Code implementation cannot reference previous implementations";
											return null;
										}
										else if (usedImpIndex > maxIndex)
										{
											tagError = "Invalid code: can't find implementation for index '" + (usedImpIndex+1) + "'";
											return null;
										}
										else
										{
											// Custom Code can't reference special implementations
											var imp = ParentShaderProperty.implementations[usedImpIndex];
											if (!ImplementationCanBeReferenced(imp))
											{
												tagError = "Invalid code: the Custom Code implementation cannot reference certain Special implementations";
												return null;
											}
										}
									}
								}
								else
								{
									Debug.LogWarning(ShaderGenerator2.ErrorMsg("Couldn't parse custom code tag content: \"" + currentPart + "\""));
								}

								currentPart = "";
							}
							else if (char.IsDigit(c))
							{
								currentPart += c;
							}
							else
							{
								tagError = "Invalid replacement tag: it should only contains digits";
								return null;
							}
						}
						//outside tag
						else
						{
							if (c == '{')
							{
								usesReplacementTags = true;
								tag = true;
								if (!string.IsNullOrEmpty(currentPart))
									parts.Add(currentPart);
								currentPart = "";
							}
							else
							{
								currentPart += c;
							}
						}
					}

					//tag not closed
					if (tag)
					{
						tagError = "Invalid code: replacement tag isn't closed";
						return null;
					}

					//add last part if any
					if (!string.IsNullOrEmpty(currentPart))
						parts.Add(currentPart);

					return new ReplacementTagsResult()
					{
						parts = parts,
						usedImplementations = usedImps
					};
				}

				bool ImplementationCanBeReferenced(Implementation imp)
				{
					// Custom Code can't reference special implementations
					if (!ParentShaderProperty.deferredSampling && imp is Imp_GenericFromTemplate)
					{
						if (!((Imp_GenericFromTemplate)imp).WorksWithCustomCode)
						{
							return false;
						}
					}
					else if (!ParentShaderProperty.deferredSampling
						&& (imp is Imp_HSV || imp is Imp_CustomCode))
					{
						return false;
					}

					return true;
				}
			}

			[Serialization.SerializeAs("imp_hsv")]
			public class Imp_HSV : Implementation
			{
				public static VariableType VariableCompatibility { get { return VariableType.color | VariableType.color_rgba; } }
				public static string MenuLabel { get { return "Special/HSV"; } }
				internal override string GUILabel() { return MenuLabel; }
				internal override bool HasOperator() { return false; }
				internal override OptionFeatures[] NeededFeatures() { return new[] { hsvType == HsvType.FullOffset ? OptionFeatures.HSV_Full : (hsvType == HsvType.Colorize ? OptionFeatures.HSV_Colorize : OptionFeatures.HSV_Grayscale) }; }
				internal override string[] NeededFeaturesExtra() { return new[] { string.Format("HSV_COLORIZE_{0}", GetColorizeChannels()) }; }

				public enum HsvType
				{
					FullOffset,
					SaturationOffset,
					Colorize
				}

				public override bool HasErrors { get { return base.HasErrors | isFirstImplementation | noColorizeChannels; } }

				[Serialization.SerializeAs("type")] HsvType hsvType;
				[Serialization.SerializeAs("chue")] bool colorizeHue;
				[Serialization.SerializeAs("csat")] bool colorizeSat;
				[Serialization.SerializeAs("cval")] bool colorizeVal;

				string hueVariable;
				string saturationVariable;
				string valueVariable;
				VariableType variableType;
				bool isFirstImplementation;
				bool noColorizeChannels { get { return (hsvType == HsvType.Colorize && !colorizeHue && !colorizeSat && !colorizeVal); } }

				public Imp_HSV(ShaderProperty shaderProperty) : base(shaderProperty)
				{
					bool hasHue = hsvType == HsvType.FullOffset || (hsvType == HsvType.Colorize && colorizeHue);
					bool hasSat = hsvType != HsvType.Colorize || (hsvType == HsvType.Colorize && colorizeSat);
					bool hasVal = hsvType == HsvType.FullOffset || (hsvType == HsvType.Colorize && colorizeVal);

					if (hasHue)
						hueVariable = string.Format("_{0}_hue", ToLowerCamelCase(shaderProperty.Name));
					if (hasSat)
						saturationVariable = string.Format("_{0}_sat", ToLowerCamelCase(shaderProperty.Name));
					if (hasVal)
						valueVariable = string.Format("_{0}_val", ToLowerCamelCase(shaderProperty.Name));

					variableType = shaderProperty.Type;

					shaderProperty.onImplementationsChanged += onImplementationsChanged;
					CheckValidity();
				}

				public override void WillBeRemoved()
				{
					base.WillBeRemoved();
					ParentShaderProperty.onImplementationsChanged -= onImplementationsChanged;
				}

				void onImplementationsChanged()
				{
					CheckValidity();
				}

				void CheckValidity()
				{
					if (ParentShaderProperty.implementations.Count < 2 || ParentShaderProperty.implementations[0] == this)
					{
						isFirstImplementation = true;
					}
					else
					{
						isFirstImplementation = false;
					}

					ParentShaderProperty.CheckErrors();
				}

				internal override string PrintProperty(string indent)
				{
					var prop = base.PrintProperty(indent);

					bool hasHue = hsvType == HsvType.FullOffset || (hsvType == HsvType.Colorize && colorizeHue);
					bool hasSat = hsvType != HsvType.Colorize || (hsvType == HsvType.Colorize && colorizeSat);
					bool hasVal = hsvType == HsvType.FullOffset || (hsvType == HsvType.Colorize && colorizeVal);
					bool group = (hasHue && hasSat) || (hasHue && hasVal) || (hasSat && hasVal);

					string propName = ParentShaderProperty.Name;

					if (group)
					{
						prop += string.Format("\n{0}[HideInInspector] __BeginGroup_HSV_{1} (\"{2} HSV\", Float) = 0", indent, ToLowerCamelCase(ParentShaderProperty.Name), ParentShaderProperty.Name);
						propName = "";
					}
					else
					{
						propName = propName + " ";
					}

					if (hasHue)
						prop += string.Format("\n{0}{1} (\"{2}Hue\", Range(-180,180)) = 0", indent, hueVariable, propName);
					if (hasSat)
						prop += string.Format("\n{0}{1} (\"{2}Saturation\", Range(-2,2)) = {3}", indent, saturationVariable, propName, hsvType == HsvType.SaturationOffset ? "1.0" : "0.0");
					if (hasVal)
						prop += string.Format("\n{0}{1} (\"{2}Value\", Range(-2,2)) = 0", indent, valueVariable, propName);

					if (group)
						prop += string.Format("\n{0}[HideInInspector] __EndGroup (\"{1} HSV\", Float) = 0", indent, ParentShaderProperty.Name);

					return prop;
				}

				internal override string PrintVariableDeclare(string indent)
				{
					bool hasHue = hsvType == HsvType.FullOffset || (hsvType == HsvType.Colorize && colorizeHue);
					bool hasSat = hsvType != HsvType.Colorize || (hsvType == HsvType.Colorize && colorizeSat);
					bool hasVal = hsvType == HsvType.FullOffset || (hsvType == HsvType.Colorize && colorizeVal);

					var variables = base.PrintVariableDeclare(indent);
					if (hasHue)
						variables += string.Format("\n{0}float {1};", indent, hueVariable);
					if (hasSat)
						variables += string.Format("\n{0}float {1};", indent, saturationVariable);
					if (hasVal)
						variables += string.Format("\n{0}float {1};", indent, valueVariable);
					return variables.TrimStart('\n');
				}

				public string PrintVariableHSV(string currentReplacement)
				{
					if (hsvType == HsvType.FullOffset)
					{
						return string.Format("ApplyHSV_{0}({1}, {2}, {3}, {4})", VariableTypeToChannelsCount(variableType), currentReplacement, hueVariable, saturationVariable, valueVariable);
					}
					else if (hsvType == HsvType.Colorize)
					{
						var colorizeArguments = "";
						if (colorizeHue)
							colorizeArguments += hueVariable + ",";
						if (colorizeSat)
							colorizeArguments += " " + saturationVariable + ",";
						if (colorizeVal)
							colorizeArguments += " " + valueVariable;

						return string.Format("Colorize{0}({1}, {2})", GetColorizeChannels(), currentReplacement, colorizeArguments.TrimEnd(',').TrimStart());
					}
					else
						return string.Format("ApplyHSVGrayscale({0}, {1})", currentReplacement, saturationVariable);
				}

				string GetColorizeChannels()
				{
					return string.Format("{0}{1}{2}", colorizeHue ? "H" : "", colorizeSat ? "S" : "", colorizeVal ? "V" : "");
				}

				internal override void NewLineGUI(bool usedByCustomCode)
				{
					BeginHorizontal();
					ShaderGenerator2.ContextualHelpBox("Applies hue, saturation, value correction to this Shader Property.\nThe HSV modifier will be applied to all implementations that preceed it.\nThe corresponding material properties to adjust each HSV value will be automatically created.");
					EndHorizontal();

					BeginHorizontal();
					ShaderGenerator2.ContextualHelpBox("Modes:\n<b>Full Offset:</b> apply an offset to all H,S,V values\n<b>Saturation Offset:</b> apply an offset to the saturation only (faster code)\n<b>Colorize:</b> set the absolute value of any H,S,V value");
					EndHorizontal();

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? hsvType != default(HsvType) : hsvType != GetDefaultImplementation<Imp_HSV>().hsvType;
						SGUILayout.InlineLabel("HSV Mode", highlighted);
						hsvType = (HsvType)SGUILayout.EnumPopup(hsvType);
					}
					EndHorizontal();

					if (hsvType == HsvType.Colorize)
					{
						BeginHorizontal();
						{
							bool highlighted = !IsDefaultImplementation ? colorizeHue : colorizeHue != GetDefaultImplementation<Imp_HSV>().colorizeHue;
							SGUILayout.InlineLabel("Hue", highlighted);
							colorizeHue = SGUILayout.Toggle(colorizeHue);
						}
						EndHorizontal();

						BeginHorizontal();
						{
							bool highlighted = !IsDefaultImplementation ? colorizeSat : colorizeSat != GetDefaultImplementation<Imp_HSV>().colorizeSat;
							SGUILayout.InlineLabel("Saturation", highlighted);
							colorizeSat = SGUILayout.Toggle(colorizeSat);
						}
						EndHorizontal();

						BeginHorizontal();
						{
							bool highlighted = !IsDefaultImplementation ? colorizeVal : colorizeVal != GetDefaultImplementation<Imp_HSV>().colorizeVal;
							SGUILayout.InlineLabel("Value", highlighted);
							colorizeVal = SGUILayout.Toggle(colorizeVal);
						}
						EndHorizontal();
					}

					if (HasErrors)
					{
						if (isFirstImplementation)
						{
							BeginHorizontal();
							{
								TCP2_GUI.HelpBoxLayout("HSV can't be the first implementation, because it applies to all the previous implementations before it.", MessageType.Error);
							}
							EndHorizontal();
						}

						if (noColorizeChannels)
						{
							BeginHorizontal();
							{
								TCP2_GUI.HelpBoxLayout("You need to select the HSV channel(s) to colorize", MessageType.Error);
							}
							EndHorizontal();
						}
					}
				}
			}

			[Serialization.SerializeAs("imp_spref")]
			public class Imp_ShaderPropertyReference : Implementation
			{
				public static VariableType VariableCompatibility { get { return VariableTypeAll; } }
				public static string MenuLabel { get { return "Other Shader Property"; } }
				internal override string GUILabel() { return MenuLabel; }

				[Serialization.SerializeAs("cc")] public int ChannelsCount = 3;
				[Serialization.SerializeAs("chan")] public string Channels = "RGB";
				[Serialization.SerializeAs("lsp")] public string LinkedShaderPropertyName;
				string DefaultChannels = "RGB";

				public List<ShaderProperty> Dependencies = new List<ShaderProperty>();

				ShaderProperty _linkedShaderProperty;
				public ShaderProperty LinkedShaderProperty
				{
					get { return _linkedShaderProperty; }
					set
					{
						SetLinkedShaderProperty(value);
					}
				}

				public override string ToHashString()
				{
					var result = new StringBuilder();

					var props = GetType().GetProperties();
					foreach (var prop in props)
					{
						var attributes = prop.GetCustomAttributes(typeof(Serialization.SerializeAsAttribute), true);
						if (attributes == null || attributes.Length == 0)
						{
							continue;
						}

						if (prop.PropertyType == typeof(ShaderProperty))
						{
							var spRef = (ShaderProperty)prop.GetValue(this, null);
							result.Append(spRef != null ? spRef.Name : "EmptyShaderPropertyRef");
						}
						else
						{
							result.Append(prop.GetValue(this, null));
						}
					}

					var fields = GetType().GetFields();
					foreach (var field in fields)
					{
						if (field.Name == "guid") continue;
						result.Append(field.GetValue(this));
					}

					return result.ToString();
				}

				public override bool HasErrors
				{
					get
					{
						return base.HasErrors | _linkedShaderProperty == null | (_linkedShaderProperty != null && !_linkedShaderProperty.IsVisible());
					}
				}

				public Imp_ShaderPropertyReference(ShaderProperty shaderProperty) : base(shaderProperty)
				{
					InitChannelsCount();
				}

				void InitChannelsCount()
				{
					switch (ParentShaderProperty.Type)
					{
						case VariableType.@float: ChannelsCount = 1; break;
						case VariableType.float2: ChannelsCount = 2; break;
						case VariableType.color:
						case VariableType.float3: ChannelsCount = 3; break;
						case VariableType.color_rgba:
						case VariableType.float4: ChannelsCount = 4; break;
					}
				}

				public override Implementation Clone(string suffix = null)
				{
					var mp = (Imp_ShaderPropertyReference)base.Clone();
					return mp;
				}

				public override void OnPasted()
				{
					InitChannelsCount();
					TryToFindLinkedShaderProperty();
				}

				public void TryToFindLinkedShaderProperty()
				{
					if (string.IsNullOrEmpty(LinkedShaderPropertyName))
					{
						return;
					}

					if (ShaderGenerator2.CurrentConfig == null)
					{
						return;
					}

					var match = Array.Find(ShaderGenerator2.CurrentConfig.VisibleShaderProperties, sp => sp.Name == LinkedShaderPropertyName);
					if (match != null)
					{
						SetLinkedShaderProperty(match);
					}
				}

				internal override string PrintVariableFragment(string inputSource, string outputSource, string arguments)
				{
					var hideChannels = TryGetArgument("hide_channels", arguments);
					var channels = string.IsNullOrEmpty(hideChannels) ? "." + Channels.ToLowerInvariant() : "";

					if (LinkedShaderProperty.IsUsedInLightingFunction && ShaderGenerator2.CurrentPassHasLightingFunction)
						return string.Format("{0}.{1}{2}", outputSource, LinkedShaderProperty.GetVariableName(), channels);
					else
						return string.Format("{0}{1}", LinkedShaderProperty.GetVariableName(), channels);
				}

				internal override void NewLineGUI(bool usedByCustomCode)
				{
					BeginHorizontal();
					ShaderGenerator2.ContextualHelpBox("Reference another Shader Property as a source for this one.\nFor example, you could reference the Albedo's alpha channel as a source mask for another property like specular.");
					EndHorizontal();

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? false : LinkedShaderPropertyName != GetDefaultImplementation<Imp_ShaderPropertyReference>().LinkedShaderPropertyName;
						SGUILayout.InlineLabel("Shader Property", highlighted);

						if (GUILayout.Button((LinkedShaderProperty != null) ? LinkedShaderProperty.Name : "None", SGUILayout.Styles.ShurikenPopup))
						{
							var menu = CreateShaderPropertiesMenu(this.ParentShaderProperty, this.LinkedShaderProperty, OnSelectShaderProperty);
							if (menu != null)
							{
								menu.ShowAsContext();
							}
						}
					}
					EndHorizontal();

					GUILayout.Space(3);

					if (LinkedShaderProperty != null)
					{
						int SourceChannelsCount = 0;
						bool sourceIsColor = false;
						switch (LinkedShaderProperty.Type)
						{
							case VariableType.@float:
								SourceChannelsCount = 1;
								break;

							case VariableType.float2:
								SourceChannelsCount = 2;
								break;

							case VariableType.color:
								sourceIsColor = true;
								SourceChannelsCount = 3;
								break;

							case VariableType.float3:
								SourceChannelsCount = 3;
								break;

							case VariableType.float4:
								SourceChannelsCount = 4;
								break;

							case VariableType.color_rgba:
								sourceIsColor = true;
								SourceChannelsCount = 4;
								break;
						}

						BeginHorizontal();
						{
							bool highlighted = !IsDefaultImplementation ? Channels != DefaultChannels : Channels != GetDefaultImplementation<Imp_ShaderPropertyReference>().Channels;
							SGUILayout.InlineLabel("Swizzle", highlighted);

							if (usedByCustomCode)
							{
								using (new EditorGUI.DisabledScope(true))
								{
									GUILayout.Label(TCP2_GUI.TempContent("Defined in Custom Code"), SGUILayout.Styles.ShurikenValue, GUILayout.Height(16), GUILayout.ExpandWidth(false));
								}
							}
							else
							{

								string optionsStr = sourceIsColor ? "RGBA" : "XYZW";
								optionsStr = optionsStr.Substring(0, SourceChannelsCount);
								if (ChannelsCount == 1)
									Channels = SGUILayout.GenericSelector(optionsStr, Channels);
								else
									Channels = SGUILayout.GenericSwizzle(Channels, ChannelsCount, optionsStr);
							}
						}
						EndHorizontal();

						// errors
						if (_linkedShaderProperty == null)
						{
							BeginHorizontal();
							{
								TCP2_GUI.HelpBoxLayout("No Shader Property defined.", MessageType.Error);
							}
							EndHorizontal();
						}
						else if (!_linkedShaderProperty.IsVisible())
						{
							BeginHorizontal();
							{
								TCP2_GUI.HelpBoxLayout("Invalid Shader Property defined.", MessageType.Error);
							}
							EndHorizontal();
						}
					}
				}

				public static GenericMenu CreateShaderPropertiesMenu(ShaderProperty parent, ShaderProperty selected, GenericMenu.MenuFunction2 selectCallback)
				{
					var menu = new GenericMenu();
					var shaderProperties = new List<ShaderProperty>(ShaderGenerator2.CurrentConfig.VisibleShaderProperties);
					shaderProperties.Sort((x, y) => string.Compare(x.Name, y.Name));
					if (shaderProperties != null && shaderProperties.Count > 0)
					{
						foreach (var sp in shaderProperties)
						{
							if (sp == parent)
								continue;

							string referenceError = IsReferencePossible(parent, sp);

							if (referenceError != "")
							{
								if (referenceError != null)
									menu.AddDisabledItem(new GUIContent(sp.Name + " " + referenceError));
								else
									menu.AddItem(new GUIContent(sp.Name), selected == sp, selectCallback, sp);
							}
						}
						return menu;
					}

					return null;
				}

				static bool CheckCyclicReferences(ShaderProperty parent, ShaderProperty reference)
				{
					//check cyclic references
					bool cyclic = false;
					foreach (var imp in reference.implementations)
					{
						var impSpRef = imp as Imp_ShaderPropertyReference;
						if (impSpRef != null)
						{
							if (impSpRef.Dependencies.Contains(parent))
							{
								return true;
							}
							else
							{
								foreach (var dependency in impSpRef.Dependencies)
								{
									cyclic |= CheckCyclicReferences(parent, dependency);
								}
							}
						}

						var impMpTex = imp as Imp_MaterialProperty_Texture;
						if (impMpTex != null && impMpTex.UvSource == Imp_MaterialProperty_Texture.UvSourceType.OtherShaderProperty)
						{
							if (impMpTex.Dependencies.Contains(parent))
							{
								return true;
							}
							else
							{
								foreach (var dependency in impMpTex.Dependencies)
								{
									cyclic |= CheckCyclicReferences(parent, dependency);
								}
							}
						}
					}
					return cyclic;
				}

				/// <summary>
				/// Verify that 'parent' can reference 'reference'
				/// </summary>
				/// <returns>null if the reference is allowed, an error message if not, an empty string if the reference should be hidden in the menus</returns>
				public static string IsReferencePossible(ShaderProperty parent, ShaderProperty reference)
				{
					// Clones now copy the passBitmask, but for backward compatibility we need
					// to retrieve the source of the clone and fetch its passBitmask directly
					if (parent.isLayerClone)
					{
						string sourceName = parent.Name.Substring(0, parent.Name.LastIndexOf('_'));
						var sourceSp = ShaderGenerator2.CurrentConfig.GetShaderPropertyByName(sourceName);
						if (sourceSp != null)
						{
							parent.passBitmask = sourceSp.passBitmask;
						}
					}
					
					//can't reference (from) a hook
					if (parent.isHook || reference.isHook)
						return "";
					//can't reference a fixed function value
					if (reference.Program == ProgramType.FixedFunction)
						return "";
					//disable properties that have a different bitmask (used in a different pass, so can't cross reference)
					if (parent.passBitmask != reference.passBitmask)
						return "(different pass)";
					//can't reference between vertex & fragment shaders
					if (parent.Program != reference.Program)
						return string.Format("({0} shader)", reference.Program.ToString().ToLowerInvariant());
					//cyclic reference
					if (CheckCyclicReferences(parent, reference))
						return "(cyclic reference)";
					// deferred sampling
					if (!string.IsNullOrEmpty(reference.preventReference))
						return reference.preventReference;

					return null;
				}

				void OnSelectShaderProperty(object sp)
				{
					LinkedShaderProperty = sp as ShaderProperty;
					ParentShaderProperty.CheckHash();
					ShaderGenerator2.NeedsHashUpdate = true;
				}

				void SetLinkedShaderProperty(ShaderProperty shaderProperty)
				{
					if (shaderProperty == LinkedShaderProperty)
						return;

					if (shaderProperty == ParentShaderProperty)
					{
						Debug.LogError(ShaderGenerator2.ErrorMsg("Shader Property Referenced implementation tried to reference its parent: '" + shaderProperty.Name + "'"));
						return;
					}

					//build dependencies list to check cyclic references
					Dependencies.Clear();
					foreach (var imp in shaderProperty.implementations)
					{
						var impSpRef = imp as Imp_ShaderPropertyReference;
						if (impSpRef != null)
							Dependencies.AddRange(impSpRef.Dependencies);
					}
					if (Dependencies.Contains(shaderProperty))
					{
						//cyclic reference: can happen if a template has incorrect values
						Debug.LogError(ShaderGenerator2.ErrorMsg("Cyclic reference between '" + this.ParentShaderProperty.Name + "' and '" + shaderProperty.Name + "'"));
						return;
					}
					Dependencies.Add(shaderProperty);

					//assign as new linked shader property
					_linkedShaderProperty = shaderProperty;
					LinkedShaderPropertyName = _linkedShaderProperty == null ? "" : _linkedShaderProperty.Name;

					if (shaderProperty == null)
					{
						Debug.LogError(ShaderGenerator2.ErrorMsg("Referenced ShaderProperty is null"));
						return;
					}

					//determine default swizzle value based on channels count & linked shader property available channels
					bool sourceIsColor = shaderProperty.Type == VariableType.color || shaderProperty.Type == VariableType.color_rgba;
					string options = sourceIsColor ? "RGBA" : "XYZW";
					switch (shaderProperty.Type)
					{
						case VariableType.@float: options = "X"; break;
						case VariableType.float2: options = "XY"; break;
						case VariableType.float3: options = "XYZ"; break;
						case VariableType.float4: options = "XYZW"; break;
						case VariableType.color: options = "RGB"; break;
						case VariableType.color_rgba: options = "RGBA"; break;
					}

					// set default channels, or preserve existing ones as far as possible (the implementation could have just been deserialized)
					var prevChannels = Channels;
					Channels = "";
					for (int i = 0; i < ChannelsCount; i++)
					{
						if (prevChannels != null && i < prevChannels.Length && options.Contains(prevChannels[i].ToString()))
							Channels += prevChannels[i];
						else
							Channels += options[i % options.Length];
					}
					DefaultChannels = Channels;
				}

				//Force updating the Shader Property hash once we've retrieved the correct Linked Shader Property
				public void ForceUpdateParentDefaultHash()
				{
					ParentShaderProperty.ForceUpdateDefaultHash();
				}
			}

			[Serialization.SerializeAs("imp_ct")]
			public class Imp_CustomMaterialProperty : Implementation
			{
				public static VariableType VariableCompatibility { get { return VariableTypeAll; } }
				public static string MenuLabel { get { return "Custom Material Property"; } }
				internal override string GUILabel() { return MenuLabel; }

				internal override OptionFeatures[] NeededFeatures()
				{
					if (LinkedCustomMaterialProperty != null)
					{
						return LinkedCustomMaterialProperty.NeededFeatures();
					}
					else
					{
						return base.NeededFeatures();
					}
				}

				CustomMaterialProperty _linkedCustomMaterialProperty;
				public CustomMaterialProperty LinkedCustomMaterialProperty
				{
					get { return _linkedCustomMaterialProperty; }
					set
					{
						_linkedCustomMaterialProperty = value;
						LinkedCustomMaterialPropertyName = _linkedCustomMaterialProperty == null ? "" : _linkedCustomMaterialProperty.PropertyName;
					}
				}
				[Serialization.SerializeAs("lct")] public string LinkedCustomMaterialPropertyName;
				[Serialization.SerializeAs("cc")] public int ChannelsCount = 4;
				[Serialization.SerializeAs("chan")] public string Channels = "RGBA";
				[Serialization.SerializeAs("avchan")] string AvailableChannels = "RGBA";
				string DefaultChannels = "RGBA";

				public override bool HasErrors { get { return base.HasErrors | LinkedCustomMaterialProperty == null | errorMessage != null; } }
				string errorMessage = null;
				public override void CheckErrors()
				{
					base.CheckErrors();

					// Specific combinations errors
					errorMessage = null;
					if (this.LinkedCustomMaterialProperty != null)
					{
						var imp_texture = this.LinkedCustomMaterialProperty.implementation as Imp_MaterialProperty_Texture;

						if (this.ParentShaderProperty.Program == ProgramType.Vertex
							&& imp_texture != null
							&& imp_texture.UvSource == Imp_MaterialProperty_Texture.UvSourceType.ScreenSpace)
						{
							// TODO is that still true?
							errorMessage = "You can't use a texture with screen-space UV on a vertex Shader Property.";
						}

						/*
						if (this.ParentShaderProperty.Program == ProgramType.Vertex
							&& imp_texture != null
							&& imp_texture.UseWorldPosUV)
						{
							errorMessage = "You can't use a texture with world position UV on a vertex Shader Property.";
						}
						*/
					}
				}

				public Imp_CustomMaterialProperty(ShaderProperty shaderProperty) : base(shaderProperty)
				{
					InitChannelsCount();
					InitChannelsSwizzle();
				}

				void InitChannelsCount()
				{
					switch (ParentShaderProperty.Type)
					{
						case VariableType.@float: ChannelsCount = 1; break;
						case VariableType.float2: ChannelsCount = 2; break;
						case VariableType.color:
						case VariableType.float3: ChannelsCount = 3; break;
						case VariableType.color_rgba:
						case VariableType.float4: ChannelsCount = 4; break;
					}
				}

				public void InitChannelsSwizzle()
				{
					Channels = LinkedCustomMaterialProperty != null ? LinkedCustomMaterialProperty.GetChannelsForVariableType(ParentShaderProperty.Type) : "-";
					DefaultChannels = Channels;
					UpdateAvailableChannels();
				}

				void UpdateAvailableChannels()
				{
					if (LinkedCustomMaterialProperty == null)
					{
						AvailableChannels = "-";
						return;
					}

					string channels = LinkedCustomMaterialProperty.Channels;
					// hacky way to extract unique characters only
					string tmp = "";
					for (int i = 0; i < channels.Length; i++)
					{
						if (!tmp.Contains(channels[i].ToString()))
						{
							tmp += channels[i];
						}
					}
					AvailableChannels = tmp;
				}

				public override void OnPasted()
				{
					InitChannelsCount();
				}

				public bool willBeRemoved { get; private set; }
				public override void WillBeRemoved()
				{
					this.willBeRemoved = true;
					if (LinkedCustomMaterialProperty != null)
					{
						LinkedCustomMaterialProperty.implementation.WillBeRemoved();
					}
					base.WillBeRemoved();
				}

				internal override string PrintVariableFragment(string inputSource, string outputSource, string arguments)
				{
					var hideChannels = TryGetArgument("hide_channels", arguments);
					var channels = string.IsNullOrEmpty(hideChannels) ? "." + Channels.ToLowerInvariant() : "";

					if (ParentShaderProperty.IsUsedInLightingFunction && ShaderGenerator2.IsInLightingFunction)
					{
						return string.Format("surface.{0}{1}", LinkedCustomMaterialProperty.PrintVariableFragment(), channels);
					}
					
					return string.Format("{0}{1}", LinkedCustomMaterialProperty.PrintVariableFragment(), channels);
				}

				internal override string PrintVariableVertex(string inputSource, string outputSource, string arguments)
				{
					var hideChannels = TryGetArgument("hide_channels", arguments);
					var channels = string.IsNullOrEmpty(hideChannels) ? "." + Channels.ToLowerInvariant() : "";
					return string.Format("{0}{1}", LinkedCustomMaterialProperty.PrintVariableVertex(), channels);
				}

				internal override void NewLineGUI(bool usedByCustomCode)
				{
					BeginHorizontal();
					ShaderGenerator2.ContextualHelpBox("Reference a Custom Material Property for this Shader Property. This is an easy way to define material properties that can be reused across the shader.\nFor example, you can embed 4 different masks into one texture, each mask being mapped to the R,G,B,A channels.");
					EndHorizontal();

					BeginHorizontal();
					{
						SGUILayout.InlineLabel("Custom Property");

						var rect = EditorGUILayout.GetControlRect();
						if (GUI.Button(rect, (LinkedCustomMaterialProperty != null) ? LinkedCustomMaterialProperty.Label : "None", SGUILayout.Styles.ShurikenPopup))
						{
							var menu = CreateCustomMaterialPropertiesMenu(LinkedCustomMaterialProperty, OnSelectCustomTexture);
							menu.ShowAsContext();
						}
					}
					EndHorizontal();

					GUILayout.Space(3);

					BeginHorizontal();
					{
						bool highlighted = !IsDefaultImplementation ? Channels != DefaultChannels : Channels != GetDefaultImplementation<Imp_CustomMaterialProperty>().Channels;
						SGUILayout.InlineLabel("Swizzle", highlighted);

						if (usedByCustomCode)
						{
							using (new EditorGUI.DisabledScope(true))
							{
								GUILayout.Label(TCP2_GUI.TempContent("Defined in Custom Code"), SGUILayout.Styles.ShurikenValue, GUILayout.Height(16), GUILayout.ExpandWidth(false));
							}
						}
						else
						{
							if (ChannelsCount == 1)
								Channels = SGUILayout.GenericSelector(AvailableChannels, Channels);
							else
								Channels = SGUILayout.GenericSwizzle(Channels, ChannelsCount, AvailableChannels);
						}
					}
					EndHorizontal();

					if (LinkedCustomMaterialProperty == null)
					{
						BeginHorizontal();
						TCP2_GUI.HelpBoxLayout("No Custom Material Property defined!", MessageType.Error);
						EndHorizontal();
					}

					if (errorMessage != null)
					{
						BeginHorizontal();
						{
							TCP2_GUI.HelpBoxLayout(errorMessage, MessageType.Error);
						}
						EndHorizontal();
					}
				}

				internal static GenericMenu CreateCustomMaterialPropertiesMenu(CustomMaterialProperty selected, GenericMenu.MenuFunction2 callback)
				{
					var customTextures = ShaderGenerator2.CurrentConfig.CustomMaterialProperties;
					var menu = new GenericMenu();

					if (customTextures != null && customTextures.Length > 0)
					{
						foreach (var ct in customTextures)
						{
							menu.AddItem(new GUIContent(string.Format("{0} ({1})", ct.Label, ct.PropertyName)), selected == ct, callback, ct);
						}
						return menu;
					}

					menu.AddDisabledItem(new GUIContent("No Custom Material Property defined"));
					return menu;
				}

				void OnSelectCustomTexture(object ct)
				{
					var customTexture = ct as CustomMaterialProperty;
					LinkedCustomMaterialProperty = customTexture;
					UpdateChannels();
					ShaderGenerator2.PushUndoState();
				}

				public void UpdateChannels()
				{
					UpdateAvailableChannels();

					// check that the current Channels only contains characters from the new available channels
					foreach (char c in Channels)
					{
						bool ok = false;
						foreach(var c2 in AvailableChannels)
						{
							if (c == c2)
							{
								ok = true;
								break;
							}
						}

						if (!ok)
						{
							InitChannelsSwizzle();
							return;
						}
					}
				}
			}
		}
	}
}
