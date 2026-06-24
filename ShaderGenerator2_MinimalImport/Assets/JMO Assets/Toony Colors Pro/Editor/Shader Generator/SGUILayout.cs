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
// Merged from ReorderableLayoutList.cs
// -----------------------------------------------------------------------------

public class ReorderableLayoutList
{
	const float DRAG_WIDTH = 20;

	int bufferedDraggedElement = -1;
	int draggedElement = -1;
	Vector2 mouseDragOrigin;
	float mouseDragOffset;
	Rect[] elementRects;

	float yOffset;
	float lastSwappedHeight;    //last swapped element height
	float draggedHeight;        //currently dragged element height

	int swappedElementAnimation = -1;
	float swappedElementPosOffset = 0f;
	float swappedElementOffset = 0f;
	float swappedElementAnimationTime = 0f;
	const float kSwappedElementDuration = 0.2f;
	bool pendingReorderChange;

	public delegate void NeedRepaint();
	public static event NeedRepaint OnNeedRepaint;

	private static void Repaint()
	{
		if(OnNeedRepaint != null)
			OnNeedRepaint();
	}

	public bool DoLayoutList(Action<int, float> DrawElement, IList list, float dragWidth = DRAG_WIDTH)
	{
		return DoLayoutList(DrawElement, list, new RectOffset(0, 0, 0, 0), dragWidth);
	}

	Vector2 mousePosition;
	static GUIStyle _dragHandle;
	static GUIStyle dragHandle
	{
		get
		{
			if(_dragHandle == null)
			{
				_dragHandle = "RL DragHandle";
			}
			return _dragHandle;
		}
	}
	static readonly int hash_dragHandle = "RL DragHandle".GetHashCode();

	// Returns 'true' when elements have been reordered
	public bool DoLayoutList(Action<int, float> DrawElement, IList list, RectOffset padding, float dragWidth = DRAG_WIDTH)
	{
		bool canBeDragged = list != null && list.Count > 1;

		if(Event.current.type != EventType.Layout)
			mousePosition = Event.current.mousePosition;

		var guiColor = GUI.color;

		if(elementRects == null || elementRects.Length != list.Count)
		{
			elementRects = new Rect[list.Count];
		}

		//lambda function so that we can reorder drawing when one is selected
		Action<int> DrawListItem = i =>
		{
			float mouseDelta = 0;

			if(draggedElement == i)
			{
				//offset ui drawing based on mouse delta
				mouseDelta = mouseDragOrigin.y + mouseDragOffset - mousePosition.y;

				//block at the top/bottom of the ui
				float yMax = mouseDragOffset;
				float yMin = 0;
				for(var j = 0; j < list.Count; j++)
				{
					if(j < i)
						yMax += elementRects[j].height;
					else if(j > i)
						yMin -= elementRects[j].height;
				}
				mouseDelta = Mathf.Clamp(mouseDelta, yMin, yMax);

				//negative space to offset the ui freely
				GUILayout.Space(-mouseDelta);
			}
			else if(swappedElementAnimation == i)
			{
				//swapped element animation: slide towards target position
				float delta = Mathf.Clamp01((Time.realtimeSinceStartup - swappedElementAnimationTime) / kSwappedElementDuration);
				//simple easing animation (ease out quad)
				System.Func<float, float> animationEasing = (x) => { return -1f * x * (x-2); };
				swappedElementOffset = Mathf.Lerp(swappedElementPosOffset, 0, animationEasing(delta));
				GUILayout.Space(-swappedElementOffset);
			}

			//get dragging rect
			var dragRect = EditorGUILayout.BeginVertical();
			{
				if (draggedElement == i)
				{
					var c = EditorGUIUtility.isProSkin ? 0.2f : 0.75f;
					GUI.color *=  new Color(c, c, c, 0.85f);
					EditorGUI.DrawRect(dragRect, Color.white);
					GUI.color = guiColor;
				}

				//build array of draggable rectangle zones
				if (draggedElement < 0 && Event.current.type == EventType.Repaint)
				{
					elementRects[i] = dragRect;
				}

				dragRect.xMin += padding.left;
				dragRect.width = dragWidth - 2;
				dragRect.xMax -= padding.right;
				dragRect.yMin += padding.top;
				dragRect.yMax -= padding.bottom;

				//dragging zone UI
				var drawRect = dragRect;
				drawRect.yMin += 7;
				drawRect.yMax -= 4;

				//draw drag handle icons
				if (Event.current.type == EventType.Repaint)
				{
					//ui color to indicate we are dragging this implementation
					if (!canBeDragged)
					{
						GUI.color *= new Color(1, 1, 1, .25f);
					}
					if (draggedElement == i)
					{
						GUI.color *= new Color(.8f, .8f, .8f);
					}

					const float dragHeight = 6;
					var count = Mathf.FloorToInt(drawRect.height / dragHeight);
					count = Mathf.Max(1, count);
					var margin = drawRect.height - count*dragHeight;
					for (var j = 0; j < count; j++)
					{
						var dragIconRect = drawRect;
						dragIconRect.xMin += 5;
						dragIconRect.xMax -= 5;
						dragIconRect.height = dragHeight;
						dragIconRect.y = drawRect.y + (j*dragHeight) + margin/2f;
						dragHandle.Draw(dragIconRect, GUIContent.none, hash_dragHandle);
					}

					GUI.color = guiColor;
				}

				//change cursor when over drag zone
				if (canBeDragged)
				{
					if (draggedElement > -1)
						EditorGUIUtility.AddCursorRect(dragRect, MouseCursor.Pan);
					else
						EditorGUIUtility.AddCursorRect(dragRect, MouseCursor.MoveArrow);
				}

				//callback to GUI drawing, including margin
				DrawElement(i, dragWidth + 2);
			}
			EditorGUILayout.EndVertical();

			//listen to mouse drag events
			if (canBeDragged)
			{
				if (Event.current.type == EventType.MouseDown && dragRect.Contains(mousePosition))
				{
					bufferedDraggedElement = i;
					mouseDragOrigin = mousePosition;
					lastSwappedHeight = elementRects[i].height;
					draggedHeight = elementRects[i].height;
					GUIUtility.keyboardControl = 0;
					GUIUtility.hotControl = 0;
					Repaint();
				}
			}

			if(draggedElement == i)
			{
				//compensate offset
				GUILayout.Space(mouseDelta);
			}
			else if(swappedElementAnimation == i)
			{
				//swapped element animation: slide towards target position
				GUILayout.Space(swappedElementOffset);
				Repaint();
			}
		};

		// catch stop dragging events now before they could be used
		bool stopDrag = false;
		if (Event.current.type == EventType.MouseUp || Event.current.rawType == EventType.MouseUp)
		{
			stopDrag = true;
		}

		for (var i = 0; i < list.Count; i++)
		{
			if(draggedElement == i)
			{
				//leave space for dragged imp: will be drawn last
				GUILayout.Space(draggedHeight);
				if (Event.current.type == EventType.Layout)
				{
					yOffset = 0;
				}
			}
			else
			{
				DrawListItem(i);

				if (Event.current.type == EventType.Layout)
				{
					yOffset += elementRects[i].height;
				}
			}
		}

		//draw the dragged imp last so that it is in front of the other ones
		if(draggedElement > -1)
		{
			GUILayout.Space(-(yOffset + draggedHeight));
			DrawListItem(draggedElement);
			GUILayout.Space(yOffset);
		}

		//need to apply the dragged imp after the loop to prevent gui layout mismatch errors
		if(Event.current.isMouse)
		{
			draggedElement = bufferedDraggedElement;
		}

		//mouse drag event: swap the implementations if mouse is inside a particular imp rect
		if(draggedElement > -1 && Event.current.type == EventType.MouseDrag)
		{
			//repaint window
			Repaint();

			for(var i = 0; i < elementRects.Length; i++)
			{
				if(elementRects[i].Contains(mousePosition) && draggedElement != i)
				{
					//swap the list items
					var tmp = list[i];
					list[i] = list[draggedElement];
					list[draggedElement] = tmp;

					//compensate y diff for mouseOrigin
					var diff = elementRects[i].y - elementRects[draggedElement].y;
					mouseDragOrigin.y += diff;

					//compensate size difference between swapped implementations
					var heightDiff = lastSwappedHeight - elementRects[i].height;
					lastSwappedHeight = elementRects[i].height;
					mouseDragOffset -= heightDiff;

					//set the animated swapped element
					swappedElementAnimation = draggedElement;
					swappedElementAnimationTime = Time.realtimeSinceStartup;
					//going up
					if((draggedElement > i))
						swappedElementPosOffset = -mouseDragOffset - diff;
					//going down
					else
						swappedElementPosOffset = mouseDragOffset - elementRects[i].height;

					//swap current dragged imp
					bufferedDraggedElement = i;
					draggedElement = i;

					pendingReorderChange = true;
				}
			}
		}

		// stop dragging : needs to be at the end to prevent GUI mismatch errors
		if(stopDrag)
		{
			bufferedDraggedElement = -1;
			draggedElement = -1;
			mouseDragOffset = 0f;
			Repaint();

			if(pendingReorderChange)
			{
				pendingReorderChange = false;
				return true;
			}
		}

		return false;
	}
}

// -----------------------------------------------------------------------------
// Merged from Tooltip.cs
// -----------------------------------------------------------------------------

namespace ToonyColorsPro
{
	public class Tooltip : EditorWindow
	{
		static bool assemblyReload;
		static Tooltip instance;
		static GUIContent guiContent = new GUIContent();
		static float closeTime;
		const float closeDelay = 0.1f;
		static bool updateEvent;
		static bool isHiding;
		static Rect _labelRect = new Rect();

		static GUIStyle _style;
		static GUIStyle style
		{
			get
			{
				if (_style == null)
				{
					_style = new GUIStyle(EditorStyles.wordWrappedLabel);
					_style.richText = true;
					_style.alignment = TextAnchor.MiddleLeft;
				}
				return _style;
			}
		}

		public static void Show(Vector2 position, string message)
		{
			Show(position, 250, message);
		}

		public static void Show(Vector2 position, float width, string message)
		{
			if (instance == null)
			{
				var windows = Resources.FindObjectsOfTypeAll<Tooltip>();

				if (windows.Length > 0)
				{
					// destroy any lingering window
					for (int i = 1; i < windows.Length; i++)
					{
						windows[i].Close();
						DestroyImmediate(windows[i]);
					}

					instance = windows[0];
				}
				else
				{
					instance = CreateInstance<Tooltip>();
					instance.minSize = Vector2.zero;
				}
			}


			const float padding = 4.0f;

			guiContent.text = message.Replace("  ", "\n");
			float height = style.CalcHeight(guiContent, width) + padding;
			instance.position = new Rect(position.x, position.y, width + padding, height);
			_labelRect.x = padding / 2.0f;
			_labelRect.width = width;
			_labelRect.height = instance.position.height;
			instance.ShowPopup();
			isHiding = false;
		}

		public static void Hide()
		{
			if (!isHiding && instance != null)
			{
				isHiding = true;
				closeTime = Time.realtimeSinceStartup + closeDelay;

				if (!updateEvent)
				{
					EditorApplication.update += applicationUpdate;
					updateEvent = true;
				}
			}
		}

		static void applicationUpdate()
		{
			if (Time.realtimeSinceStartup > closeTime)
			{
				instance.Close();

				EditorApplication.update -= applicationUpdate;
				updateEvent = false;
			}
		}

		void OnGUI()
		{
			// draw background
			EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), new Color(1,1,1,0.1f));

			// draw border
			EditorGUI.DrawRect(new Rect(0, 0, position.width, 1), Color.black);
			EditorGUI.DrawRect(new Rect(0, 0, 1, position.height), Color.black);
			EditorGUI.DrawRect(new Rect(position.width-1, 0, 1, position.height), Color.black);
			EditorGUI.DrawRect(new Rect(0, position.height-1, position.width, 1), Color.black);

			// label
			GUI.Label(_labelRect, guiContent, style);
		}
	}
}

// -----------------------------------------------------------------------------
// Merged from SGUILayout.cs
// -----------------------------------------------------------------------------

//Extended GUILayout for Shader Generator 2

namespace ToonyColorsPro
{
	namespace ShaderGenerator
	{
		static class SGUILayout
		{
			public static float Indent = 0f;

			//--------------------------------------------------------------------------------------------------------------------------------
			// UI Constants

			public static class Constants
			{
				public const string screenSpaceUVLabel = "Screen Space";
				public const string worldPosUVLabel = "World Position";
				public const string triplanarUVLabel = "Triplanar";
				public const string shaderPropertyUVLabel = "Other Shader Property";
				public const string customMaterialPropertyUVLabel = "Custom Material Property";

				public static readonly string[] DefaultTextureValues =
				{
					"white",
					"black",
					"gray",
					"bump"
				};

				public static readonly string[] UvChannelOptions =
				{
					"texcoord0",
					"texcoord1",
					"texcoord2",
					"texcoord3",
					screenSpaceUVLabel,
					worldPosUVLabel,
					triplanarUVLabel,
					shaderPropertyUVLabel,
					customMaterialPropertyUVLabel
				};

				public static readonly string[] UvChannelOptionsVertex =
				{
					"texcoord0",
					"texcoord1",
					"texcoord2",
					"texcoord3",
					worldPosUVLabel,
					triplanarUVLabel,
					shaderPropertyUVLabel,
					customMaterialPropertyUVLabel
				};

				public static string[] LockedUvChannelOptions =
				{
					"computed in shader"
				};

				public static readonly string[] UvAnimationOptions =
				{
					"Off",
					"Scrolling",
					"Random Offset",
					"Sine Distortion"
				};
			}

			//--------------------------------------------------------------------------------------------------------------------------------
			// GUIStyles

			internal static class Styles
			{
#if UNITY_2019_3_OR_NEWER
				public const float shurikenLineHeight = 13;
#else
				public const float shurikenLineHeight = 16;
#endif

				internal static Color colorFieldBorderColor = new Color(0, 0, 0, 0.17f);
				internal static Color colorFieldBorderColorHover = new Color(0, 0, 0, 0.5f);
				internal static Color colorFieldBorderColorPro = new Color(0, 0, 0, 0.4f);
				internal static Color colorFieldBorderColorHoverPro = new Color(1, 1, 1, 0.22f);

				static GUIStyle _GrayLabel;
				internal static GUIStyle GrayLabel
				{
					get
					{
						if(_GrayLabel == null)
						{
							var color = EditorGUIUtility.isProSkin ? new Color32(130, 130, 130, 255) : new Color32(100, 100, 100, 255);
							_GrayLabel = new GUIStyle(EditorStyles.label);
							_GrayLabel.normal.textColor = color;
							_GrayLabel.active.textColor = color;
							_GrayLabel.focused.textColor = color;
							_GrayLabel.hover.textColor = color;
						}
						return _GrayLabel;
					}
				}

				internal static Color OrangeColor { get { return EditorGUIUtility.isProSkin ? new Color32(250, 130, 0, 255) : new Color32(200, 100, 20, 255); } }

				static GUIStyle _OrangeBoldLabel;
				internal static GUIStyle OrangeBoldLabel
				{
					get
					{
						if(_OrangeBoldLabel == null)
						{
							var color = OrangeColor;
							_OrangeBoldLabel = new GUIStyle(EditorStyles.label);
							_OrangeBoldLabel.normal.textColor = color;
							_OrangeBoldLabel.active.textColor = color;
							_OrangeBoldLabel.focused.textColor = color;
							_OrangeBoldLabel.hover.textColor = color;
							_OrangeBoldLabel.fontStyle = FontStyle.Bold;
						}
						return _OrangeBoldLabel;
					}
				}

				static GUIStyle _OrangeHeader;
				internal static GUIStyle OrangeHeader
				{
					get
					{
						if(_OrangeHeader == null)
						{
							_OrangeHeader = new GUIStyle(OrangeBoldLabel);
							_OrangeHeader.fontSize = 16;
						}
						return _OrangeHeader;
					}
				}

				static GUIStyle _GrayBoldLabel;
				internal static GUIStyle GrayBoldLabel
				{
					get
					{
						if(_GrayBoldLabel == null)
						{
							_GrayBoldLabel = new GUIStyle(GrayLabel);
							_GrayBoldLabel.fontStyle = FontStyle.Bold;
						}
						return _GrayBoldLabel;
					}
				}

				static GUIStyle _GrayMiniLabel;
				internal static GUIStyle GrayMiniLabel
				{
					get
					{
						if(_GrayMiniLabel == null)
						{
							_GrayMiniLabel = new GUIStyle("ShurikenLabel")
							{
								fixedHeight = shurikenLineHeight,
								padding = new RectOffset(2, 4, 0, 0),
								fontSize = shurikenFontSize
							};
							var c = EditorGUIUtility.isProSkin ? .7f : .3f;
							_GrayMiniLabel.normal.textColor = new Color(c, c, c, 1.0f);
						}
						return _GrayMiniLabel;
					}
				}

				static GUIStyle _GrayMiniLabelWrap;
				internal static GUIStyle GrayMiniLabelWrap
				{
					get
					{
						if (_GrayMiniLabelWrap == null)
						{
							_GrayMiniLabelWrap = new GUIStyle(GrayMiniLabel)
							{
								wordWrap = true,
								fixedHeight = 0,
								stretchHeight = false,
								stretchWidth = false
							};
						}
						return _GrayMiniLabelWrap;
					}
				}

				static GUIStyle _GrayMiniLabelWrapHighlighted;
				internal static GUIStyle GrayMiniLabelWrapHighlighted
				{
					get
					{
						if (_GrayMiniLabelWrapHighlighted == null)
						{
							_GrayMiniLabelWrapHighlighted = new GUIStyle(GrayMiniLabelWrap)
							{
								fontStyle = FontStyle.Bold
							};
							var textColor = EditorGUIUtility.isProSkin ? new Color(0.0f, 0.574f, 0.488f) : new Color(0.03f, 0.46f, 0.4f);
							_GrayMiniLabelWrapHighlighted.normal.textColor = textColor;
						}
						return _GrayMiniLabelWrapHighlighted;
					}
				}


				static GUIStyle _GrayMiniBoldLabel;
				internal static GUIStyle GrayMiniBoldLabel
				{
					get
					{
						if(_GrayMiniBoldLabel == null)
						{
							_GrayMiniBoldLabel = new GUIStyle(GrayMiniLabel)
							{
								fontStyle = FontStyle.Bold
							};
						}
						return _GrayMiniBoldLabel;
					}
				}

				static GUIStyle _GrayMiniLabelHighlighted;
				internal static GUIStyle GrayMiniLabelHighlighted
				{
					get
					{
						if (_GrayMiniLabelHighlighted == null)
						{
							_GrayMiniLabelHighlighted = new GUIStyle(GrayMiniLabel)
							{
								fontStyle = FontStyle.Bold
							};

							var textColor = EditorGUIUtility.isProSkin ? new Color(0.0f, 0.574f, 0.488f) : new Color(0.03f, 0.46f, 0.4f);
							_GrayMiniLabelHighlighted.normal.textColor = textColor;
						}
						return _GrayMiniLabelHighlighted;
					}
				}

				private static GUIStyle _GrayMiniFoldout;
				public static GUIStyle GrayMiniFoldout
				{
					get
					{
						if (_GrayMiniFoldout == null)
						{
							_GrayMiniFoldout = new GUIStyle(EditorStyles.foldout);

							var grayMiniLabel = GrayMiniLabel;
							_GrayMiniFoldout.alignment = grayMiniLabel.alignment;
							_GrayMiniFoldout.font = grayMiniLabel.font;
							_GrayMiniFoldout.fontStyle = grayMiniLabel.fontStyle;
							_GrayMiniFoldout.margin = grayMiniLabel.margin;
							_GrayMiniFoldout.padding = new RectOffset(16, 0, 0, 0);
							_GrayMiniFoldout.richText = grayMiniLabel.richText;
							_GrayMiniFoldout.stretchHeight = grayMiniLabel.stretchHeight;
							_GrayMiniFoldout.stretchWidth = grayMiniLabel.stretchWidth;
							_GrayMiniFoldout.fixedHeight = 0;
							_GrayMiniFoldout.fixedWidth = 0;

							_GrayMiniFoldout.normal.textColor = grayMiniLabel.normal.textColor;
							_GrayMiniFoldout.onNormal.textColor = grayMiniLabel.normal.textColor;
							_GrayMiniFoldout.focused.textColor = grayMiniLabel.normal.textColor;
							_GrayMiniFoldout.onFocused.textColor = grayMiniLabel.normal.textColor;
							_GrayMiniFoldout.hover.textColor = grayMiniLabel.normal.textColor;
							_GrayMiniFoldout.onHover.textColor = grayMiniLabel.normal.textColor;

							var gray = EditorGUIUtility.isProSkin ? 0.4f : 0.45f;
							var textColorActive = new Color(gray, gray, gray);
							_GrayMiniFoldout.active.textColor = textColorActive;
							_GrayMiniFoldout.onActive.textColor = textColorActive;

							_GrayMiniFoldout.normal.background = TCP2_GUI.GetCustomTexture("TCP2_FoldoutArrowRight");
							_GrayMiniFoldout.active.background = _GrayMiniFoldout.normal.background;
							_GrayMiniFoldout.focused.background = _GrayMiniFoldout.normal.background;
							_GrayMiniFoldout.hover.background = _GrayMiniFoldout.normal.background;

							_GrayMiniFoldout.onNormal.background = TCP2_GUI.GetCustomTexture("TCP2_FoldoutArrowDown");
							_GrayMiniFoldout.onActive.background = _GrayMiniFoldout.onNormal.background;
							_GrayMiniFoldout.onFocused.background = _GrayMiniFoldout.onNormal.background;
							_GrayMiniFoldout.onHover.background = _GrayMiniFoldout.onNormal.background;

						}
						return _GrayMiniFoldout;
					}
				}

				static GUIStyle _GrayMiniFoldoutHighlighted;
				internal static GUIStyle GrayMiniFoldoutHighlighted
				{
					get
					{
						if (_GrayMiniFoldoutHighlighted == null)
						{
							_GrayMiniFoldoutHighlighted = new GUIStyle(GrayMiniFoldout)
							{
								fontStyle = FontStyle.Bold,
							};

							var textColor = EditorGUIUtility.isProSkin ? new Color(0.0f, 0.574f, 0.488f) : new Color(0.03f, 0.46f, 0.4f);
							_GrayMiniFoldoutHighlighted.normal.textColor = textColor;
							_GrayMiniFoldoutHighlighted.active.textColor = textColor;
							_GrayMiniFoldoutHighlighted.focused.textColor = textColor;
							_GrayMiniFoldoutHighlighted.hover.textColor = textColor;
							_GrayMiniFoldoutHighlighted.onNormal.textColor = textColor;
							_GrayMiniFoldoutHighlighted.onActive.textColor = textColor;
							_GrayMiniFoldoutHighlighted.onFocused.textColor = textColor;
							_GrayMiniFoldoutHighlighted.onHover.textColor = textColor;
						}
						return _GrayMiniFoldoutHighlighted;
					}
				}

				static GUIStyle _GrayInlineLabel;
				internal static GUIStyle GrayInlineLabel
				{
					get
					{
						if(_GrayInlineLabel == null)
						{
							_GrayInlineLabel = new GUIStyle(GrayLabel);
						}
						return _GrayInlineLabel;
					}
				}

				static GUIStyle _LineStyle;
				internal static GUIStyle LineStyle
				{
					get
					{
						if(_LineStyle == null)
						{
							_LineStyle = new GUIStyle();
							_LineStyle.normal.background = EditorGUIUtility.whiteTexture;
							_LineStyle.stretchWidth = true;
						}

						return _LineStyle;
					}
				}

				// ----------------------------------------------------------------
				// SHURIKEN STYLES OVERRIDES

				const int shurikenFontSize = 10;

				static GUIStyle _ShurikenValue;
				internal static GUIStyle ShurikenValue
				{
					get
					{
						if (_ShurikenValue == null)
						{
							_ShurikenValue = new GUIStyle("ShurikenValue")
							{
								fontSize = shurikenFontSize
							};
						}
						return _ShurikenValue;
					}
				}
				
				static GUIStyle _ShurikenValueMonospace;
				internal static GUIStyle ShurikenValueMonospace
				{
					get
					{
						if (_ShurikenValueMonospace == null)
						{
							_ShurikenValueMonospace = new GUIStyle(ShurikenValue);
						}
						return _ShurikenValueMonospace;
					}
				}

				static GUIStyle _ShurikenPopup;
				internal static GUIStyle ShurikenPopup
				{
					get
					{
						if (_ShurikenPopup == null)
						{
							_ShurikenPopup = new GUIStyle("ShurikenPopup")
							{
								fontSize = shurikenFontSize,
								clipping = TextClipping.Clip
							};
						}
						return _ShurikenPopup;
					}
				}

				static GUIStyle _ShurikenToggle;
				internal static GUIStyle ShurikenToggle
				{
					get
					{
						if (_ShurikenToggle == null)
						{
							_ShurikenToggle = new GUIStyle("ShurikenToggle")
							{
								fontSize = shurikenFontSize
							};
						}
						return _ShurikenToggle;
					}
				}

				static GUIStyle _ShurikenTextArea;
				internal static GUIStyle ShurikenTextArea
				{
					get
					{
						if (_ShurikenTextArea == null)
						{
							_ShurikenTextArea = new GUIStyle(ShurikenValue)
							{
								fixedHeight = 0,
								alignment = TextAnchor.UpperLeft
							};
						}
						return _ShurikenTextArea;
					}
				}

				static GUIStyle _ShurikenTextAreaMonospace;
				internal static GUIStyle ShurikenTextAreaMonospace
				{
					get
					{
						if (_ShurikenTextAreaMonospace == null)
						{
							_ShurikenTextAreaMonospace = new GUIStyle(ShurikenTextArea);
						}
						return _ShurikenTextAreaMonospace;
					}
				}

				static GUIStyle _ShurikenObjectField;
				internal static GUIStyle ShurikenObjectField
				{
					get
					{
						if (_ShurikenObjectField == null)
						{
							_ShurikenObjectField = new GUIStyle(EditorStyles.objectField)
							{
								fixedHeight = shurikenLineHeight,
								fontSize = shurikenFontSize
							};
						}
						return _ShurikenObjectField;
					}
				}

				// For custom channels selector
				static GUIStyle _ShurikenMiniButtonCustom;
				internal static GUIStyle ShurikenMiniButtonCustom
				{
					get
					{
						if (_ShurikenMiniButtonCustom == null)
						{
							_ShurikenMiniButtonCustom = new GUIStyle(EditorStyles.miniButton)
							{
								fixedWidth = 30,
								fixedHeight = 13,
								fontSize = shurikenFontSize,
								border = new RectOffset(2,2,2,2)
							};
							var margin = _ShurikenMiniButtonCustom.margin;
							margin.top -= 3;
							_ShurikenMiniButtonCustom.margin = margin;
						}
						return _ShurikenMiniButtonCustom;
					}
				}

				static GUIStyle _ShurikenMiniButtonFlexible;
				internal static GUIStyle ShurikenMiniButtonFlexible
				{
					get
					{
						if (_ShurikenMiniButtonFlexible == null)
						{
							_ShurikenMiniButtonFlexible = new GUIStyle(ShurikenMiniButtonCustom);
							_ShurikenMiniButtonFlexible.fixedWidth = 0;
						}
						return _ShurikenMiniButtonFlexible;
					}
				}

#if UNITY_2019_3_OR_NEWER
				const int MINI_BUTTON_FONT_SIZE = 10;
#endif
				
				static GUIStyle _MiniButtonLeft;
				internal static GUIStyle MiniButtonLeft
				{
					get
					{
#if !UNITY_2019_3_OR_NEWER
						return EditorStyles.miniButtonLeft;
#else
						if (_MiniButtonLeft == null)
						{
							_MiniButtonLeft = new GUIStyle(EditorStyles.miniButtonLeft){ fontSize = MINI_BUTTON_FONT_SIZE };
						}
						return _MiniButtonLeft;
#endif
					}
				}
				static GUIStyle _MiniButtonMid;
				internal static GUIStyle MiniButtonMid
				{
					get
					{
#if !UNITY_2019_3_OR_NEWER
						return EditorStyles.miniButtonMid;
#else

						if (_MiniButtonMid == null)
						{
							_MiniButtonMid = new GUIStyle(EditorStyles.miniButtonMid){ fontSize = MINI_BUTTON_FONT_SIZE };
						}
						return _MiniButtonMid;
#endif
					}
				}

				static GUIStyle _MiniButtonRight;
				internal static GUIStyle MiniButtonRight
				{
					get
					{
#if !UNITY_2019_3_OR_NEWER
						return EditorStyles.miniButtonRight;
#else
						if (_MiniButtonRight == null)
						{
							_MiniButtonRight = new GUIStyle(EditorStyles.miniButtonRight){ fontSize = MINI_BUTTON_FONT_SIZE };
						}
						return _MiniButtonRight;
#endif
					}
				}
				
				static GUIStyle _MiniButton;
				internal static GUIStyle MiniButton
				{
					get
					{
#if !UNITY_2019_3_OR_NEWER
						return EditorStyles.miniButton;
#else
						if (_MiniButton == null)
						{
							_MiniButton = new GUIStyle(EditorStyles.miniButton){ fontSize = MINI_BUTTON_FONT_SIZE };
						}
						return _MiniButton;
#endif
					}
				}
			}

			//--------------------------------------------------------------------------------------------------------------------------------
			// GUILayout-like Methods

			public static Rect GetControlRect(GUIStyle style, float height = Styles.shurikenLineHeight, float width = 0f)
			{
				return GUILayoutUtility.GetRect(width, height, style);
			}

			static string RGBAOptions = "RGBA";
			public static char RGBASelector(char currentChannel)
			{
				return GenericSelector(RGBAOptions, currentChannel);
			}
			public static string RGBASelector(string currentChannel)
			{
				return RGBASelector(currentChannel[0]).ToString();
			}

			static string XYZWOptions = "XYZW";
			public static char XYZWSelector(char currentChannel)
			{
				return GenericSelector(XYZWOptions, currentChannel);
			}
			public static string XYZWSelector(string currentChannel)
			{
				return XYZWSelector(currentChannel[0]).ToString();
			}

			static string XYZOptions = "XYZ";
			public static char XYZSelector(char currentChannel)
			{
				return GenericSelector(XYZOptions, currentChannel);
			}
			public static string XYZSelector(string currentChannel)
			{
				return XYZSelector(currentChannel[0]).ToString();
			}

			public static string GenericSelector(string options, string current, float buttonWidth = 25)
			{
				return GenericSelector(options, current[0], buttonWidth).ToString();
			}
			public static char GenericSelector(string options, char current, float buttonWidth = 25)
			{
				var upperCurrent = char.ToUpperInvariant(current);
				var selected = options.IndexOf(upperCurrent);
				if(selected < 0) selected = 0;

#if !UNITY_2019_3_OR_NEWER
				float w = buttonWidth;
#else
				float w = Styles.ShurikenMiniButtonCustom.fixedWidth;
#endif
				for (var i = 0; i < options.Length; i++)
				{
#if !UNITY_2019_3_OR_NEWER
					var rect = GUILayoutUtility.GetRect(GUIContent.none, TCP2_GUI.ShurikenMiniButton, GUILayout.Height(15), GUILayout.Width(w));
					rect.height = 12;
					rect.y -= 1; //small hack to align with the shuriken ui components

					//button style
					var style = TCP2_GUI.ShurikenMiniButton;
					if(options.Length == 2)
						style = (i == 0) ? TCP2_GUI.ShurikenMiniButtonLeft : TCP2_GUI.ShurikenMiniButtonRight;
					else if(options.Length > 1)
						style = (i == 0) ? TCP2_GUI.ShurikenMiniButtonLeft : (i == (options.Length-1) ? TCP2_GUI.ShurikenMiniButtonRight : TCP2_GUI.ShurikenMiniButtonMid);
#else
					var rect = GetControlRect(Styles.ShurikenMiniButtonCustom, width: w);
					var style = Styles.ShurikenMiniButtonCustom;
#endif

					if (GUI.Toggle(rect, selected == i, options[i].ToString(), style))
					{
						selected = i;
					}
				}
				return options[selected];
			}

			public static string RGBASwizzle(string selected, int channelsCount)
			{
				return GenericSwizzle(selected, channelsCount, "RGBA");
			}

			public static string XYZWSwizzle(string selected, int channelsCount)
			{
				return GenericSwizzle(selected, channelsCount, "XYZW");
			}

			public static string XYZSwizzle(string selected, int channelsCount)
			{
				return GenericSwizzle(selected, channelsCount, "XYZ");
			}

			public static string GenericSwizzle(string selected, int channelsCount, string options, float width = 50, bool showAvailableChannels = true)
			{
				EditorGUI.BeginChangeCheck();
				Rect rect = GetControlRect(Styles.ShurikenValue, width: width);
				var newSelected = EditorGUI.DelayedTextField(rect, selected, Styles.ShurikenValue);
				if(EditorGUI.EndChangeCheck())
				{
					// empty string
					if (newSelected.Length == 0)
					{
						return selected;
					}
					
					// not enough characters
					if (newSelected.Length < channelsCount)
					{
						// expand the last valid character
						char lastChar = newSelected[newSelected.Length - 1];
						newSelected += new string(lastChar, channelsCount - newSelected.Length);
					}

					// remove extra characters
					if (newSelected.Length > channelsCount)
					{
						newSelected = newSelected.Substring(0, channelsCount);
					}

					newSelected = newSelected.ToUpperInvariant();
					foreach(var c in newSelected)
					{
						if (!options.Contains(c.ToString()))
						{
							return selected;
						}
					}
				}

				if (showAvailableChannels)
				{
					GUILayout.Space(4);
					GUILayout.Label(string.Format("(available channels: {0})", options), Styles.GrayMiniLabel);
				}

				return newSelected.ToUpperInvariant();
			}

			static int foldoutHash = "TCP2 Foldout".GetHashCode();
			public static bool Foldout(bool foldout, string label, string tooltip = null, bool highlighted = false)
			{
				return Foldout(foldout, TCP2_GUI.TempContent(label, tooltip), highlighted);
			}
			public static bool Foldout(bool foldout, string label, bool highlighted)
			{
				return Foldout(foldout, TCP2_GUI.TempContent(label), highlighted);
			}
			public static bool Foldout(bool foldout, GUIContent label, bool highlighted = false, float width = 130)
			{
				GUILayout.Space(Indent);

				var rect = GUILayoutUtility.GetRect(label, highlighted ? Styles.GrayMiniLabelHighlighted : Styles.GrayMiniLabel, GUILayout.Height(Styles.shurikenLineHeight), GUILayout.Width(width));
				bool hover = rect.Contains(Event.current.mousePosition);

				if (hover)
				{
					EditorGUI.DrawRect(rect, Color.black * 0.1f);
				}

				label.text = string.Format("{0} {1}", foldout ? "▼" : "►", label.text);
				InlineLabel(rect, label, highlighted);

				int controlId = GUIUtility.GetControlID(foldoutHash, FocusType.Keyboard, rect);

				if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && hover)
				{
					Event.current.Use();
					GUIUtility.hotControl = controlId;
				}

				if (GUIUtility.hotControl == controlId && Event.current.type == EventType.MouseUp && Event.current.button == 0 && hover)
				{
					Event.current.Use();
					GUI.changed = true;
					GUIUtility.hotControl = 0;
					return !foldout;
				}
				return foldout;
			}

			public static Rect InlineLabel(string label, string tooltip = null, bool highlight = false)
			{
				return InlineLabel(TCP2_GUI.TempContent(label, tooltip), highlight);
			}
			public static Rect InlineLabel(string label, bool highlight)
			{
				return InlineLabel(TCP2_GUI.TempContent(label), highlight);
			}
			public static Rect InlineLabel(GUIContent label, bool highlight = false, float width = 130)
			{
				GUILayout.Space(Indent);
				var rect = GUILayoutUtility.GetRect(label, highlight ? Styles.GrayMiniLabelHighlighted : Styles.GrayMiniLabel, GUILayout.Height(Styles.shurikenLineHeight), GUILayout.Width(width));
#if !UNITY_2019_3_OR_NEWER
				rect.y -= 2;
#endif
				GUI.Label(rect, label, highlight ? Styles.GrayMiniLabelHighlighted : Styles.GrayMiniLabel);
				return rect;
			}
			public static Rect InlineLabel(Rect rect, GUIContent label, bool highlight = false, float width = 130)
			{
				GUILayout.Space(Indent);
#if !UNITY_2019_3_OR_NEWER
				rect.y -= 2;
#endif
				GUI.Label(rect, label, highlight ? Styles.GrayMiniLabelHighlighted : Styles.GrayMiniLabel);
				return rect;
			}

			public static void InlineHeader(string label, string tooltip = null)
			{
				InlineHeader(TCP2_GUI.TempContent(label, tooltip));
			}
			public static void InlineHeader(GUIContent label)
			{
				GUILayout.Space(Indent);
				var rect = GUILayoutUtility.GetRect(label, Styles.GrayMiniBoldLabel);
				rect.y -= 2;
				GUI.Label(rect, label, Styles.GrayMiniBoldLabel);
			}

			//Property fields for Shader Property: UI is harmonized and easy to update
			public static Enum EnumPopup(Enum enm)
			{
				Rect rect = GetControlRect(Styles.ShurikenPopup);
				return EditorGUI.EnumPopup(rect, enm, Styles.ShurikenPopup);
			}
			public static int Popup(int index, string[] values)
			{
				Rect rect = GetControlRect(Styles.ShurikenPopup);
				return EditorGUI.Popup(rect, index, values, Styles.ShurikenPopup);
			}
			public static string TextField(string str, bool delayed = false, bool monospace = false)
			{
				Rect rect = GetControlRect(monospace ? Styles.ShurikenValueMonospace : Styles.ShurikenValue);
				return TextField(rect, str, delayed, monospace);
			}
			public static string TextField(Rect rect, string str, bool delayed = false, bool monospace = false)
			{
				var style = monospace ? Styles.ShurikenValueMonospace : Styles.ShurikenValue;
				if (delayed)
				{
					return EditorGUI.DelayedTextField(rect, GUIContent.none, str, style);
				}
				else
				{
					return EditorGUI.TextField(rect, GUIContent.none, str, style);
				}
			}
			
			static readonly List<char> ValidVariableCharacters = new List<char>("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_0123456789".ToCharArray());
			public static string TextFieldShaderVariable(Rect rect, string str)
			{
				//special version with that only accepts alphanumerical and underscore
				var result = TextField(rect, str, monospace: true);
				for (var i = result.Length - 1; i >= 0; i--)
				{
					if (!ValidVariableCharacters.Contains(result[i]))
					{
						result = result.Remove(i, 1);
					}
				}

				return result;
			}

			public static string TextArea(string str, float height = 0, bool monospace = false)
			{
				var style = monospace ? Styles.ShurikenTextAreaMonospace : Styles.ShurikenTextArea;
				return height > 0 ?
					EditorGUI.TextArea(GetControlRect(Styles.ShurikenTextArea, height), str, style) :
					EditorGUI.TextArea(GetControlRect(Styles.ShurikenTextArea), str, style);
			}
			public static T ObjectField<T>(T obj) where T : UnityEngine.Object
			{
				//return DrawProObjectField<T>(obj);
				Rect rect = GetControlRect(Styles.ShurikenObjectField);
				return (T)EditorGUI.ObjectField(rect, GUIContent.none, obj, typeof(T), false);
			}

			public static T DrawProObjectField<T>(T obj, params GUILayoutOption[] options) where T : UnityEngine.Object
			{
				int pickerID = "ShurikenObjectField".GetHashCode();

				var rect = EditorGUILayout.GetControlRect(false, Styles.shurikenLineHeight, Styles.ShurikenValue, options);
				var btnRect = rect;
				btnRect.width = 20;
				rect.xMax -= btnRect.width;
				btnRect.x += rect.width;

				GUI.Label(rect, TCP2_GUI.TempContent(obj != null ? obj.name : "None (" + typeof(T).ToString() + ")"), Styles.ShurikenValue);
				if (GUI.Button(btnRect, "...", "MiniToolbarButton"))
				{
					EditorGUIUtility.ShowObjectPicker<T>(obj, false, "", pickerID);
				}
				if (Event.current.commandName == "ObjectSelectorUpdated")
				{
					if (EditorGUIUtility.GetObjectPickerControlID() == pickerID)
					{
						obj = EditorGUIUtility.GetObjectPickerObject() as T;
					}
				}
				return obj;
			}

			public static bool ButtonPopup(string label)
			{
				return ButtonPopup(TCP2_GUI.TempContent(label));
			}
			
			public static bool ButtonPopup(GUIContent content)
			{
				return GUILayout.Button(content, Styles.ShurikenPopup, GUILayout.MinWidth(248), GUILayout.MinHeight(Styles.shurikenLineHeight));
			}
			public static int IntField(int value)
			{
				Rect rect = GetControlRect(Styles.ShurikenValue);
				return EditorGUI.IntField(rect, value, Styles.ShurikenValue);
			}
			public static int IntField(int value, int min, int max)
			{
				return Mathf.Clamp(IntField(value), min, max);
			}
			public static float FloatField(float value)
			{
				Rect rect = GetControlRect(Styles.ShurikenValue);
				return EditorGUI.FloatField(rect, value, Styles.ShurikenValue);
			}
			public static Vector2 Vector2Field(Vector2 v2) { return VectorFieldCustomStyle(v2, 2); }
			public static Vector3 Vector3Field(Vector3 v3) { return VectorFieldCustomStyle(v3, 3); }
			public static Vector4 Vector4Field(Vector4 v4) { return VectorFieldCustomStyle(v4, 4); }
			public static Color ColorField(Color c, bool alpha, bool hdr = false)
			{
				Rect rect = GetControlRect(Styles.ShurikenValue);
				Color color;
				if (EditorGUIUtility.isProSkin)
				{
					color = rect.Contains(Event.current.mousePosition) ? Styles.colorFieldBorderColorHoverPro : Styles.colorFieldBorderColorPro;
				}
				else
				{
					color = rect.Contains(Event.current.mousePosition) ? Styles.colorFieldBorderColorHover : Styles.colorFieldBorderColor;
				}
				EditorGUI.DrawRect(rect, color);

				rect.xMin++;
				rect.xMax--;
				rect.yMin++;
				rect.yMax--;

#if UNITY_2018_1_OR_NEWER
				return EditorGUI.ColorField(rect, GUIContent.none, c, false, alpha, hdr);
#else
				return EditorGUI.ColorField(rect, GUIContent.none, c, false, alpha, hdr, new ColorPickerHDRConfig(0f, 99f, 0.01010101f, 3f));
#endif
			}
			public static bool Toggle(bool toggle)
			{
				var rect = EditorGUILayout.GetControlRect(false, Styles.shurikenLineHeight, Styles.ShurikenToggle, GUILayout.MinWidth(248));
				return EditorGUI.Toggle(rect, GUIContent.none, toggle, Styles.ShurikenToggle);
			}

			static Vector4 VectorFieldCustomStyle(Vector4 vec, int channels)
			{
				EditorGUILayout.BeginHorizontal();
				if(channels > 0)
				{
					GUILayout.Label("x", Styles.GrayMiniLabel, GUILayout.ExpandWidth(false));
					vec.x = FloatField(vec.x);
				}
				if(channels > 1)
				{
					GUILayout.Label("y", Styles.GrayMiniLabel, GUILayout.ExpandWidth(false));
					vec.y = FloatField(vec.y);
				}
				if(channels > 2)
				{
					GUILayout.Label("z", Styles.GrayMiniLabel, GUILayout.ExpandWidth(false));
					vec.z = FloatField(vec.z);
				}
				if(channels > 3)
				{
					GUILayout.Label("w", Styles.GrayMiniLabel, GUILayout.ExpandWidth(false));
					vec.w = FloatField(vec.w);
				}
				EditorGUILayout.EndHorizontal();

				return vec;
			}

			public static void DrawLine()
			{
				var c = EditorGUIUtility.isProSkin ? new Color(0.15f, 0.15f, 0.15f, 1.0f) : new Color(0.5f, 0.5f, 0.5f, 1.0f);
				DrawLine(c);
			}

			public static void DrawLine(Color color)
			{
				var rect = GUILayoutUtility.GetRect(GUIContent.none, Styles.LineStyle, GUILayout.Height(1));
				if(Event.current.type == EventType.Repaint)
				{
					var guiColor = GUI.color;
					GUI.color *= color;
					Styles.LineStyle.Draw(rect, GUIContent.none, "line".GetHashCode());
					GUI.color = guiColor;
				}
			}

			static readonly GUIContent gcInspectorLock = EditorGUIUtility.IconContent("InspectorLock");
			public static void DrawLockIcon(Color color)
			{
				if (gcInspectorLock != null)
				{
					var c = GUI.color;
					GUI.color *= color;
					var lockIconRect = EditorGUILayout.GetControlRect(false, 14, GUILayout.Width(14));
					GUI.DrawTexture(lockIconRect, gcInspectorLock.image);
					GUI.color = c;
				}
			}

			public static class Utils
			{
				public static string RemoveWhitespaces(string input)
				{
					return input.Replace(" ", "");
				}

				public static string VariableNameToReadable(string input)
				{
					string output = "";

					int start = 0;
					if (input[0] == '_') start = 1;

					bool lastWasLowercase = false;
					for(int i = start; i < input.Length; i++)
					{
						if ((Char.IsUpper(input[i]) || Char.IsDigit(input[i])) && lastWasLowercase && output.Length > 0)
						{
							output += " ";
						}

						char c = input[i];
						if (c == '_') c = ' ';

						output += c;
						lastWasLowercase = Char.IsLower(input[i]);
					}

					return output;
				}
			}

			public struct IndentedLine : IDisposable
			{
				public IndentedLine(float indent = -1)
				{
					GUILayout.BeginHorizontal();
					GUILayout.Space(indent < 0 ? Indent : indent);
				}

				public void Dispose()
				{
					GUILayout.EndHorizontal();
				}
			}
		}
	}
}

// -----------------------------------------------------------------------------
// Merged from UIFeatures.cs
// -----------------------------------------------------------------------------

namespace ToonyColorsPro
{
	namespace ShaderGenerator
	{
		// Utility to generate custom Toony Colors Pro 2 shaders with specific features

		//--------------------------------------------------------------------------------------------------
		// UI from Template System

		internal class UIFeature
		{
			const float LABEL_WIDTH = 290f;
			static Rect LastPositionInline;
			static float LastLowerBoundY;
			static float LastIndentY;
			static int LastIndent;
			static bool LastVisible;

			static GUIContent tempContent = new GUIContent();
			protected static GUIContent TempContent(string label, string tooltip = null)
			{
				tempContent.text = label;
				tempContent.tooltip = tooltip;
				return tempContent;
			}

			protected string label;
			protected string tooltip;
			protected string[] requires;    //features required for this feature to be enabled (AND)
			protected string[] requiresOr;  //features required for this feature to be enabled (OR)
			protected string[] excludes;   //features required to be OFF for this feature to be enabled
			protected string[] excludesAll;   //features required to be OFF for this feature to be enabled
			protected string[] visibleIf;   //features required to be ON for this feature to be visible
			protected bool showHelp = false;
			protected int indentLevel;
			protected string helpTopic;
			protected bool customGUI;   //complete custom GUI that overrides the default behaviors (e.g. separator)
			protected bool ignoreVisibility;   //ignore the current visible state and force the UI element to be drawn
			bool wasEnabled;    //track when the Enabled flag changes
			bool inline;        //draw next to previous position
			bool halfWidth;     //draw in half space of the position (for inline)

			UIFeature parent; // simple hierarchy system to handle visibility and vertical/horizontal line hierarchy drawing

			protected static Stack<bool> FoldoutStack = new Stack<bool>();
			internal static void ClearFoldoutStack()
			{
				UIFeature_DropDownStart.ClearDropDownsList();
				FoldoutStack.Clear();
			}

			//Initialize a UIFeature given a list of arbitrary properties
			internal UIFeature(List<KeyValuePair<string, string>> list)
			{
				if(list != null)
				{
					foreach(var kvp in list)
					{
						ProcessProperty(kvp.Key, kvp.Value);
					}
				}
			}

			//Process a property from the Template in the form key=value
			protected virtual void ProcessProperty(string key, string value)
			{
				//Direct inline properties, no need for a value
				if(string.IsNullOrEmpty(value))
				{
					switch(key)
					{
						case "nohelp": showHelp = false; break;
						case "indent": indentLevel = 1; break;
						case "inline": inline = true; break;
						case "half": halfWidth = true; break;
						case "help": showHelp = true; break;
					}
				}
				else
				{
					//Common properties to all UIFeature classes
					switch(key)
					{
						case "lbl": label = value.Replace("  ", "\n").Trim('"'); break;
						case "tt": tooltip = value.Replace(@"\n", "\n").Replace("  ", "\n").Trim('"'); break;
						case "help": showHelp = true; helpTopic = value; break;
						case "indent": indentLevel = int.Parse(value); break;
						case "needs": requires = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries); break;
						case "needsOr": requiresOr = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries); break;
						case "excl": excludes = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries); break;
						case "exclAll": excludesAll = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries); break;
						case "visibleIf": visibleIf = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries); break;
						case "inline": inline = bool.Parse(value); break;
						case "half": halfWidth = bool.Parse(value); break;
					}
				}
			}

			static Rect HeaderRect(ref Rect lineRect, float width)
			{
				var rect = lineRect;
				rect.width = width;

				lineRect.x += rect.width;
				lineRect.width -= rect.width;

				return rect;
			}

			// temp state between each DrawGUI, so that children don't have to
			// re-fetch them with the Enabled() and Highlighted() methods
			bool enabled;
			bool highlighted;
			internal void DrawGUI(Config config)
			{
				bool guiEnabled = GUI.enabled;

				// update states
				this.enabled = Enabled(config);
				this.highlighted = Highlighted(config);

				GUI.enabled = enabled;

				// by default, only show if top-level
				bool visible = indentLevel == 0;
				// if set, show all
				if (GlobalOptions.data.ShowDisabledFeatures)
				{
					visible = true;
				}
				// else, show only if parent is enabled & highlighted
				else if (indentLevel > 0 && parent != null)
				{
					if (visibleIf != null && visibleIf.Length > 0)
					{
						visible = config.HasFeaturesAll(visibleIf);
					}
					else
					{
						visible = parent.enabled && parent.highlighted;
					}
				}

				if(inline)
					visible = LastVisible;

				visible &= (FoldoutStack.Count > 0) ? FoldoutStack.Peek() : true;

				ForceValue(config);

				if(customGUI)
				{
					if(visible || ignoreVisibility)
					{
						DrawGUI(new Rect(0, 0, EditorGUIUtility.currentViewWidth, 0), config, false);
						return;
					}
				}
				else if(visible)
				{
					//Total line rect
					Rect position;
					position = inline ? LastPositionInline : EditorGUILayout.GetControlRect();

					if(halfWidth)
					{
						position.width = (position.width/2f) - 8f;
					}

					//LastPosition is already halved
					if(inline)
					{
						position.x += position.width + 16f;
					}

					//Last Position for inlined properties
					LastPositionInline = position;

					if(!inline)
					{
						//Help
						if(showHelp)
						{
							var helpRect = HeaderRect(ref position, 20f);
							TCP2_GUI.HelpButtonSG2(helpRect, label, string.IsNullOrEmpty(helpTopic) ? label : helpTopic);
						}
						else
						{
							HeaderRect(ref position, 20f);
						}

						const float barIndent = 2;	// pixels for vertical bar indent
						const float uiIndent = 8;	// pixels per indent level for UI

						var horizontalRect = position;
						var lineColor = Color.black * (EditorGUIUtility.isProSkin ? 0.3f : 0.2f);
						for (int i = 1; i <= indentLevel; i++)
						{
							// vertical bar to the left of indented lines
							horizontalRect = position;
							if (indentLevel > 0 && Event.current.type == EventType.Repaint)
							{
								var verticalRect = position;
								verticalRect.width = 1;
								verticalRect.x += barIndent;
								verticalRect.yMax -= 7;
								verticalRect.yMin = (indentLevel <= 0 || i > LastIndent) ? LastLowerBoundY : LastIndentY;
								EditorGUI.DrawRect(verticalRect, lineColor);
							}

							// indent
							HeaderRect(ref position, uiIndent);

							// horizontal bar
							horizontalRect.width = horizontalRect.width - position.width;
							horizontalRect.height = 1;
							horizontalRect.xMin += barIndent + 1;
							horizontalRect.y += position.height/2;
							if (indentLevel > 0 && i == indentLevel && Event.current.type == EventType.Repaint)
							{
								EditorGUI.DrawRect(horizontalRect, lineColor);
							}
						}

						LastLowerBoundY = position.yMax;
						LastIndentY = horizontalRect.y;
						LastIndent = indentLevel;
					}

					//Label
					var guiContent = TempContent(label, tooltip);
					var labelPosition = HeaderRect(ref position, inline ? (EditorStyles.label.CalcSize(guiContent)).x + 8f : LABEL_WIDTH - position.x);
					TCP2_GUI.SubHeader(labelPosition, guiContent, this.highlighted && this.enabled);

					//Actual property
					bool labelClicked = Event.current.type == EventType.MouseUp && Event.current.button == 0 && labelPosition.Contains(Event.current.mousePosition);
					if (labelClicked)
					{
						Event.current.Use();
					}
					DrawGUI(position, config, labelClicked);

					LastVisible = visible;
				}

				GUI.enabled = guiEnabled;
			}

			//Internal DrawGUI: actually draws the feature
			protected virtual void DrawGUI(Rect position, Config config, bool labelClicked)
			{
				GUI.Label(position, "Unknown feature type for: " + label);
			}

			//Defines if the feature is selected/toggle/etc. or not
			internal virtual bool Highlighted(Config config)
			{
				return false;
			}

			//Called when processing this UIFeature, in case any forced value needs to be set even if the UI component isn't visible
			internal virtual void ForceValue(Config config)
			{

			}

			//Called when Enabled(config) has changed state
			//Originally used to force Multiple UI to enable the default feature, if any
			protected virtual void OnEnabledChangedState(Config config, bool newState)
			{

			}

			internal bool Enabled(Config config)
			{
				var enabled = true;
				if(requiresOr != null)
				{
					enabled = false;
					enabled |= config.HasFeaturesAny(requiresOr);
				}
				if(excludesAll != null)
					enabled &= !config.HasFeaturesAll(excludesAll);
				if(requires != null)
					enabled &= config.HasFeaturesAll(requires);
				if(excludes != null)
					enabled &= !config.HasFeaturesAny(excludes);

				if(wasEnabled != enabled)
				{
					OnEnabledChangedState(config, enabled);
				}
				wasEnabled = enabled;

				return enabled;
			}

			//Parses a #FEATURES text block
			internal static UIFeature[] GetUIFeatures(string[] lines, ref int i, Template template)
			{
				var uiFeaturesList = new List<UIFeature>();
				string subline;
				do
				{
					subline = lines[i];
					i++;

					//Empty line
					if(string.IsNullOrEmpty(subline))
						continue;

					var data = subline.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);

					//Skip empty or comment # lines
					if(data == null || data.Length == 0 || (data.Length > 0 && data[0].StartsWith("#")))
						continue;

					var kvpList = new List<KeyValuePair<string, string>>();
					for(var j = 1; j < data.Length; j++)
					{
						var sdata = data[j].Split('=');
						if(sdata.Length == 2)
							kvpList.Add(new KeyValuePair<string, string>(sdata[0], sdata[1]));
						else if(sdata.Length == 1)
							kvpList.Add(new KeyValuePair<string, string>(sdata[0], null));
						else
							Debug.LogError("Couldn't parse UI property from Template:\n" + data[j]);
					}

					// Discard the UIFeature if not for this template:
					var templateCompatibility = kvpList.Find(kvp => kvp.Key == "templates");
					if (templateCompatibility.Key == "templates")
					{
						if (!templateCompatibility.Value.Contains(template.id))
						{
							continue;
						}
					}

					UIFeature feature = null;
					switch(data[0])
					{
						case "---": feature = new UIFeature_Separator(); break;
						case "space": feature = new UIFeature_Space(kvpList); break;
						case "flag": feature = new UIFeature_Flag(kvpList, false); break;
						case "nflag": feature = new UIFeature_Flag(kvpList, true); break;
						case "float": feature = new UIFeature_Float(kvpList); break;
						case "int": feature = new UIFeature_Int(kvpList); break;
						case "subh": feature = new UIFeature_SubHeader(kvpList); break;
						case "header": feature = new UIFeature_Header(kvpList); break;
						case "warning": feature = new UIFeature_Warning(kvpList); break;
						case "sngl": feature = new UIFeature_Single(kvpList, false); break;
						case "nsngl": feature = new UIFeature_Single(kvpList, true); break;
						case "gpu_inst_opt": feature = new UIFeature_Single(kvpList, false); break;
						case "mult": feature = new UIFeature_Multiple(kvpList); break;
						case "mult_flags": feature = new UIFeature_MultFlags(kvpList); break;
						case "keyword": feature = new UIFeature_Keyword(kvpList); break;
						case "keyword_str": feature = new UIFeature_KeywordString(kvpList); break;
						case "dd_start": feature = new UIFeature_DropDownStart(kvpList); break;
						case "dd_end": feature = new UIFeature_DropDownEnd(); break;
						case "mult_fs": feature = new UIFeature_MultipleFixedFunction(kvpList); break;

						default: feature = new UIFeature(kvpList); break;
					}

					uiFeaturesList.Add(feature);
				}
				while(subline != "#END" && i < lines.Length);

				var uiFeaturesArray = uiFeaturesList.ToArray();

				// Build hierarchy from the parsed UIFeatures
				// note: simple hierarchy, where only a top-level element can be parent (one level only)
				UIFeature lastParent = null;
				for (int j = 0; j < uiFeaturesArray.Length; j++)
				{
					var uiFeature = uiFeaturesArray[j];
					if (uiFeature.indentLevel == 0 && !(uiFeature is UIFeature_Header) && !(uiFeature is UIFeature_Warning) && !uiFeature.inline)
					{
						lastParent = uiFeature;
					}
					else if (uiFeature.indentLevel > 0)
					{
						uiFeature.parent = lastParent;
					}
				}

				return uiFeaturesList.ToArray();
			}
		}

		//----------------------------------------------------------------------------------------------------------------------------------------------------------------
		// SINGLE FEATURE TOGGLE

		internal class UIFeature_Single : UIFeature
		{
			readonly bool negative;
			string keyword;
			string[] toggles;    //features forced to be toggled when this feature is enabled
			bool enabledByDefault;

			internal UIFeature_Single(List<KeyValuePair<string, string>> list, bool negative) : base(list)
			{
				this.negative = negative;
			}

			protected override void ProcessProperty(string key, string value)
			{
				if (key == "kw")
					keyword = value;
				else if (key == "toggles")
					toggles = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
				else if (key == "enabled_for")
				{
					var editorVersion = GetUnityVersion(Application.unityVersion);
					var targetVersion = GetUnityVersion(value.Trim('\"'));
					if (IsVersionMoreOrEqual(editorVersion, targetVersion))
					{
						enabledByDefault = true;
					}
				}
				else
					base.ProcessProperty(key, value);

				(int, int, int) GetUnityVersion(string input)
				{
					string[] parts = input.Split('.');

					string parts2 = "";
					for (int i = 0; i < parts[2].Length; i++)
					{
						if (!char.IsDigit(parts[2][i])) break;
						parts2 += parts[2][i];
					}
					parts[2] = parts2;

					int.TryParse(parts[0], out int major);
					int.TryParse(parts[1], out int minor);
					int.TryParse(parts[2], out int patch);

					return (major, minor, patch);
				}

				bool IsVersionMoreOrEqual((int, int, int) v1, (int, int, int) v2)
				{
					if (v1.Item1 < v2.Item1) return false;
					if (v1.Item1 > v2.Item1) return true;
					// major are equal

					if (v1.Item2 < v2.Item2) return false;
					if (v1.Item2 > v2.Item2) return true;
					// minor are equal

					return v1.Item3 >= v2.Item3;
				}
			}

			internal override void ForceValue(Config config)
			{
				if (enabledByDefault)
				{
					Utils.AddIfMissing(ShaderGenerator2.CurrentConfig.Features, keyword);
					enabledByDefault = false;
				}
			}

			protected override void DrawGUI(Rect position, Config config, bool labelClicked)
			{
				var feature = Highlighted(config);
				EditorGUI.BeginChangeCheck();
				if (negative)
				{
					bool check = EditorGUI.Toggle(position, !feature);
					if (GUI.changed)
					{
						feature = !check;
					}
				}
				else
				{
					feature = EditorGUI.Toggle(position, feature);
				}
				if (labelClicked)
				{
					feature = !feature;
					GUI.changed = true;
				}
				if(EditorGUI.EndChangeCheck())
				{
					config.ToggleFeature(keyword, feature);

					if(toggles != null)
					{
						foreach (var t in toggles)
						{
							config.ToggleFeature(t, feature);
						}
					}
				}
			}

			internal override bool Highlighted(Config config)
			{
				return config.HasFeature(keyword);
			}
		}

		//----------------------------------------------------------------------------------------------------------------------------------------------------------------
		// FEATURES COMBOBOX

		internal class UIFeature_Multiple : UIFeature
		{
			string[] labels;
			string[] features;
			string[] toggles;    //features forced to be toggled when this feature is enabled

			internal UIFeature_Multiple(List<KeyValuePair<string, string>> list) : base(list) { }

			protected override void ProcessProperty(string key, string value)
			{
				if(key == "kw")
				{
					var data = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
					labels = new string[data.Length];
					features = new string[data.Length];

					for(var i = 0; i < data.Length; i++)
					{
						var lbl_feat = data[i].Split('|');
						if(lbl_feat.Length != 2)
						{
							Debug.LogWarning("[UIFeature_Multiple] Invalid data:" + data[i]);
							continue;
						}

						labels[i] = lbl_feat[0];
						features[i] = lbl_feat[1];
					}
				}
				else if(key == "toggles")
					toggles = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
				else
					base.ProcessProperty(key, value);
			}

			protected override void DrawGUI(Rect position, Config config, bool labelClicked)
			{
				var feature = GetSelectedFeature(config);
				if(feature < 0) feature = 0;

				EditorGUI.BeginChangeCheck();
				feature = EditorGUI.Popup(position, feature, labels);
				if(EditorGUI.EndChangeCheck())
				{
					ToggleSelectedFeature(config, feature);
				}
			}

			int GetSelectedFeature(Config config)
			{
				for(var i = 0; i < features.Length; i++)
				{
					if(config.HasFeature(features[i]))
						return i;
				}

				return -1;
			}

			internal override bool Highlighted(Config config)
			{
				var feature = GetSelectedFeature(config);
				return feature > 0;
			}

			protected override void OnEnabledChangedState(Config config, bool newState)
			{
				var feature = -1;
				if(newState)
				{
					feature = GetSelectedFeature(config);
					if(feature < 0) feature = 0;
				}

				ToggleSelectedFeature(config, feature);
			}

			void ToggleSelectedFeature(Config config, int selectedFeature)
			{
				for(var i = 0; i < features.Length; i++)
				{
					var enable = (i == selectedFeature);
					config.ToggleFeature(features[i], enable);
				}

				if(toggles != null)
				{
					foreach(var t in toggles)
						config.ToggleFeature(t, selectedFeature > 0);
				}
			}
		}

		//----------------------------------------------------------------------------------------------------------------------------------------------------------------
		// MULT FLAGS: enum flags-like interface to select multiple flags

		internal class UIFeature_MultFlags : UIFeature
		{
			string keyword;
			string[] labels;
			string[] values;
			string cachedKeywordValue;
			List<string> flagsList = new List<string>();
			int cachedFlagListCount;

			string popupLabel = "None";

			Rect flagsMenuPosition;
			bool reopenFlagsMenu = false;

			internal UIFeature_MultFlags(List<KeyValuePair<string, string>> list) : base(list) { }

			protected override void ProcessProperty(string key, string value)
			{
				if (key == "kw")
				{
					keyword = value;
				}
				else if (key == "default")
				{
					flagsList.Add(value);
				}
				else if(key == "values")
				{
					Debug.Log("process values: " + value);
					var data = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
					labels = new string[data.Length];
					this.values = new string[data.Length];

					for(var i = 0; i < data.Length; i++)
					{
						var lbl_feat = data[i].Split('|');
						if(lbl_feat.Length != 2)
						{
							Debug.LogWarning("[UIFeature_MultFlags] Invalid data:" + data[i]);
							continue;
						}

						labels[i] = lbl_feat[0];
						this.values[i] = lbl_feat[1];
					}
				}
				else
					base.ProcessProperty(key, value);
			}

			protected override void DrawGUI(Rect position, Config config, bool labelClicked)
			{
				// update from flag lists
				if (cachedFlagListCount != flagsList.Count)
				{
					cachedFlagListCount = flagsList.Count;
					string newKeywordValue = string.Join(" ", flagsList.ToArray());
					config.SetKeyword(keyword, newKeywordValue);
					cachedKeywordValue = newKeywordValue;
					UpdateButtonLabel();
				}

				// update from config
				string configKeywordValue = config.GetKeyword(keyword);
				if (cachedKeywordValue != configKeywordValue)
				{
					cachedKeywordValue = configKeywordValue;
					flagsList.Clear();
					if (configKeywordValue != null)
					{
						var data = configKeywordValue.Split(' ');
						flagsList.AddRange(data);
					}
				}

				if (GUI.Button(position, TCP2_GUI.TempContent(popupLabel), EditorStyles.popup) || reopenFlagsMenu)
				{
					GetFlagsMenu(config, reopenFlagsMenu);
					reopenFlagsMenu = false;
				}
			}

			void GetFlagsMenu(Config config, bool reusePosition = false)
			{
				var flagsMenu = new GenericMenu();
				for (int i = 0; i < labels.Length; i++)
				{
					flagsMenu.AddItem(new GUIContent(labels[i]), flagsList.Contains(values[i]), OnSelectFlag, new object[] { config, values[i] });
				}

				if (!reusePosition)
				{
					flagsMenuPosition = new Rect(Event.current.mousePosition, Vector2.zero);
				}
				flagsMenu.DropDown(flagsMenuPosition);
			}

			void UpdateButtonLabel()
			{
				if (flagsList.Count == 0)
				{
					popupLabel = "None";
				}
				else if (flagsList.Count == 1)
				{
					int index = Array.IndexOf(values, flagsList[0]);
					popupLabel = labels[index];
				}
				else
				{
					popupLabel = "Multiple values...";
				}
			}

			void OnSelectFlag(object data)
			{
				int previousCount = flagsList.Count;

				Config config = (Config)((object[])data)[0];
				string value = (string)((object[])data)[1];

				if (flagsList.Contains(value))
				{
					flagsList.Remove(value);
				}
				else
				{
					flagsList.Add(value);
				}

				UpdateButtonLabel();
				config.SetKeyword(keyword, string.Join(" ", flagsList.ToArray()));

				reopenFlagsMenu = true;
				EditorApplication.delayCall += () =>
				{
					// will force the menu to reopen next frame
					ShaderGenerator2.RepaintWindow();
				};
			}

			internal override bool Highlighted(Config config)
			{
				return flagsList.Count > 0;
			}
		}

		//----------------------------------------------------------------------------------------------------------------------------------------------------------------
		// FEATURES COMBOBOX for FIXED FUNCTION STATES
		// Embeds some UI from the corresponding Shader Property to easily change the states in the Features tab

		internal class UIFeature_MultipleFixedFunction : UIFeature
		{
			string keyword;
			string[] labels;
			string[] fixedFunctionValues;
			string shaderPropertyName;
			ShaderProperty shaderProperty;

			internal UIFeature_MultipleFixedFunction(List<KeyValuePair<string, string>> list) : base(list) { }

			protected override void ProcessProperty(string key, string value)
			{
				if (key == "kw")
				{
					keyword = value;
				}
				else if (key == "options")
				{
					var data = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
					labels = new string[data.Length];
					fixedFunctionValues = new string[data.Length];

					for (var i = 0; i < data.Length; i++)
					{
						var lbl_feat = data[i].Split('|');
						if (lbl_feat.Length != 2)
						{
							Debug.LogWarning("[UIFeature_MultipleFixedFunction] Invalid data:" + data[i]);
							continue;
						}

						labels[i] = lbl_feat[0];
						fixedFunctionValues[i] = lbl_feat[1];
					}
				}
				else if (key == "shader_property")
				{
					shaderPropertyName = value.Replace("\"", "");
				}
				else
				{
					base.ProcessProperty(key, value);
				}
			}

			protected override void DrawGUI(Rect position, Config config, bool labelClicked)
			{
				// Fetch embedded Shader Property
				bool highlighted = Highlighted(config);
				if (shaderProperty == null && highlighted) // the SP only exists if the feature is enabled
				{
					var match = Array.Find(config.AllShaderProperties, sp => sp.Name == shaderPropertyName);
					if (match == null)
					{
						Debug.LogError(ShaderGenerator2.ErrorMsg("Can't find matching embedded Shader Property with name: '" + shaderPropertyName + "'"));
					}
					shaderProperty = match;
				}

				int feature = highlighted ? (shaderProperty.implementations[0] as ShaderProperty.Imp_Enum).ValueType + 1 : 0;
				if (feature < 0) feature = 0;

				EditorGUI.BeginChangeCheck();
				feature = EditorGUI.Popup(position, feature, labels);
				if (EditorGUI.EndChangeCheck())
				{
					config.ToggleFeature(keyword, feature > 0);

					// Update Fixed Function value type
					var ffv = fixedFunctionValues[feature];
					if (feature > 0 && !string.IsNullOrEmpty(ffv) && shaderProperty != null)
					{
						(shaderProperty.implementations[0] as ShaderProperty.Imp_Enum).SetValueTypeFromString(ffv);
						shaderProperty.CheckHash();
						shaderProperty.CheckErrors();
					}
				}

				// Show embedded Shader Property UI
				if (highlighted && shaderProperty != null)
				{
					if (shaderProperty.Type != ShaderProperty.VariableType.fixed_function_enum)
					{
						EditorGUILayout.HelpBox("Embedded Shader Property should be a Fixed Function enum.", MessageType.Error);
					}
					else
					{
						var imp = shaderProperty.implementations[0] as ShaderProperty.Imp_Enum;
						if (imp == null)
						{
							EditorGUILayout.HelpBox("First implementation of enum Shader Property isn't an Imp_Enum.", MessageType.Error);
						}
						else
						{
							EditorGUI.BeginChangeCheck();
							{
								imp.EmbeddedGUI(28, 170);
							}
							if (EditorGUI.EndChangeCheck())
							{
								shaderProperty.CheckHash();
								shaderProperty.CheckErrors();
							}
						}
					}
				}
			}

			internal override bool Highlighted(Config config)
			{
				return config.HasFeature(keyword);
			}

			/*
			protected override void OnEnabledChangedState(Config config, bool newState)
			{
				var feature = -1;
				if (newState)
				{
					feature = GetSelectedFeature(config);
					if (feature < 0) feature = 0;
				}

				ToggleSelectedFeature(config, feature);
			}
			*/
		}

		//----------------------------------------------------------------------------------------------------------------------------------------------------------------
		// KEYWORD COMBOBOX

		internal class UIFeature_Keyword : UIFeature
		{
			string keyword;
			string[] labels;
			string[] values;
			int defaultValue;
			bool forceValue;

			internal UIFeature_Keyword(List<KeyValuePair<string, string>> list) : base(list) { }

			protected override void ProcessProperty(string key, string value)
			{
				if(key == "kw")
					keyword = value;
				else if(key == "default")
					defaultValue = int.Parse(value, CultureInfo.InvariantCulture);
				else if(key == "forceKeyword")
					forceValue = bool.Parse(value);
				else if(key == "values")
				{
					var data = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
					labels = new string[data.Length];
					values = new string[data.Length];

					for(var i = 0; i < data.Length; i++)
					{
						var lbl_feat = data[i].Split('|');
						if(lbl_feat.Length != 2)
						{
							Debug.LogWarning("[UIFeature_Keyword] Invalid data:" + data[i]);
							continue;
						}

						labels[i] = lbl_feat[0];
						values[i] = lbl_feat[1];
					}
				}
				else
					base.ProcessProperty(key, value);
			}

			internal override void ForceValue(Config config)
			{
				var selectedValue = GetSelectedValue(config);
				if(selectedValue < 0)
					selectedValue = defaultValue;

				if(forceValue && Enabled(config) && !config.HasKeyword(keyword))
				{
					config.SetKeyword(keyword, values[selectedValue]);
				}
			}

			protected override void DrawGUI(Rect position, Config config, bool labelClicked)
			{
				var selectedValue = GetSelectedValue(config);
				if(selectedValue < 0)
				{
					selectedValue = defaultValue;
					if(forceValue && Enabled(config))
					{
						config.SetKeyword(keyword, values[defaultValue]);
					}
				}

				EditorGUI.BeginChangeCheck();
				selectedValue = EditorGUI.Popup(position, selectedValue, labels);
				if(EditorGUI.EndChangeCheck())
				{
					if(string.IsNullOrEmpty(values[selectedValue]))
						config.RemoveKeyword(keyword);
					else
						config.SetKeyword(keyword, values[selectedValue]);
				}
			}

			int GetSelectedValue(Config config)
			{
				var currentValue = config.GetKeyword(keyword);
				for(var i = 0; i < values.Length; i++)
				{
					if(currentValue == values[i])
						return i;
				}

				return -1;
			}

			internal override bool Highlighted(Config config)
			{
				var feature = GetSelectedValue(config);
				return feature != defaultValue;
			}
		}

		//----------------------------------------------------------------------------------------------------------------------------------------------------------------
		// KEYWORD STRING

		internal class UIFeature_KeywordString : UIFeature
		{
			string keyword;
			string defaultValue;
			bool forceValue;

			internal UIFeature_KeywordString(List<KeyValuePair<string, string>> list) : base(list) { }

			protected override void ProcessProperty(string key, string value)
			{
				switch(key)
				{
					case "kw": keyword = value; break;
					case "default": defaultValue = value.Trim('"'); break;
					case "forceKeyword": forceValue = bool.Parse(value); break;
					default: base.ProcessProperty(key, value); break;
				}
			}

			internal override void ForceValue(Config config)
			{
				if (forceValue && Enabled(config) && !config.HasKeyword(keyword))
				{
					config.SetKeyword(keyword, defaultValue);
				}
			}

			protected override void DrawGUI(Rect position, Config config, bool labelClicked)
			{
				EditorGUI.BeginChangeCheck();
				string value = config.GetKeyword(keyword);
				if (string.IsNullOrEmpty(value))
				{
					value = defaultValue;
				}
				string newValue = EditorGUI.TextField(position, GUIContent.none, value);
				if (EditorGUI.EndChangeCheck())
				{
					if (newValue != value)
					{
						config.SetKeyword(keyword, newValue);
					}
				}
			}

			internal override bool Highlighted(Config config)
			{
				var value = config.GetKeyword(this.keyword);
				return !string.IsNullOrEmpty(value) && value != defaultValue;
			}
		}

		//----------------------------------------------------------------------------------------------------------------------------------------------------------------
		// SURFACE SHADER / GENERIC FLAG

		internal class UIFeature_Flag : UIFeature
		{
			readonly bool negative;
			string keyword;
			string block = "pragma_surface_shader";
			string[] toggles;    //features forced to be toggled when this flag is enabled

			internal UIFeature_Flag(List<KeyValuePair<string, string>> list, bool negative) : base(list)
			{
				this.negative = negative;
				showHelp = false;
			}

			protected override void ProcessProperty(string key, string value)
			{
				if(key == "kw")
					keyword = value;
				else if(key == "block")
					block = value.Trim('"');
				else if(key == "toggles")
					toggles = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
				else
					base.ProcessProperty(key, value);
			}

			protected override void DrawGUI(Rect position, Config config, bool labelClicked)
			{
				var flag = Highlighted(config);
				EditorGUI.BeginChangeCheck();
				flag = EditorGUI.Toggle(position, flag);
				if (labelClicked)
				{
					flag = !flag;
					GUI.changed = true;
				}

				if(EditorGUI.EndChangeCheck())
				{
					UpdateConfig(config, flag);
				}
			}

			internal override bool Highlighted(Config config)
			{
				bool hasFlag = config.HasFlag(block, keyword);
				return negative ? !hasFlag : hasFlag;
			}

			void UpdateConfig(Config config, bool flag)
			{
				config.ToggleFlag(block, keyword, negative ? !flag : flag);

				if (toggles != null)
				{
					foreach (var t in toggles)
					{
						config.ToggleFeature(t, negative ? !flag : flag);
					}
				}
			}
		}

		//----------------------------------------------------------------------------------------------------------------------------------------------------------------
		// FIXED FLOAT

		internal class UIFeature_Float : UIFeature
		{
			string keyword;
			float defaultValue;
			float min = float.MinValue;
			float max = float.MaxValue;

			internal UIFeature_Float(List<KeyValuePair<string, string>> list) : base(list) { }

			protected override void ProcessProperty(string key, string value)
			{
				if(key == "kw")
					keyword = value;
				else if(key == "default")
					defaultValue = float.Parse(value, CultureInfo.InvariantCulture);
				else if(key == "min")
					min = float.Parse(value, CultureInfo.InvariantCulture);
				else if(key == "max")
					max = float.Parse(value, CultureInfo.InvariantCulture);
				else
					base.ProcessProperty(key, value);
			}

			protected override void DrawGUI(Rect position, Config config, bool labelClicked)
			{
				var currentValueStr = config.GetKeyword(keyword);
				var currentValue = defaultValue;
				if(!float.TryParse(currentValueStr, NumberStyles.Float, CultureInfo.InvariantCulture, out currentValue))
				{
					currentValue = defaultValue;

					//Only enforce keyword if feature is enabled
					if (Enabled(config))
					{
						config.SetKeyword(keyword, currentValue.ToString("0.0###############", CultureInfo.InvariantCulture));
					}
				}

				EditorGUI.BeginChangeCheck();
				var newValue = currentValue;
				newValue = Mathf.Clamp(EditorGUI.FloatField(position, currentValue), min, max);
				if(EditorGUI.EndChangeCheck())
				{
					if(newValue != currentValue)
					{
						config.SetKeyword(keyword, newValue.ToString("0.0###############", CultureInfo.InvariantCulture));
					}
				}
			}
		}

		//----------------------------------------------------------------------------------------------------------------------------------------------------------------
		// FIXED INTEGER

		internal class UIFeature_Int : UIFeature
		{
			string keyword;
			int defaultValue;
			int min = int.MinValue;
			int max = int.MaxValue;

			internal UIFeature_Int(List<KeyValuePair<string, string>> list) : base(list) { }

			protected override void ProcessProperty(string key, string value)
			{
				if(key == "kw")
					keyword = value;
				else if(key == "default")
					defaultValue = int.Parse(value, CultureInfo.InvariantCulture);
				else if(key == "min")
					min = int.Parse(value, CultureInfo.InvariantCulture);
				else if(key == "max")
					max = int.Parse(value, CultureInfo.InvariantCulture);
				else
					base.ProcessProperty(key, value);
			}

			protected override void DrawGUI(Rect position, Config config, bool labelClicked)
			{
				var currentValueStr = config.GetKeyword(keyword);
				var currentValue = defaultValue;
				if(!int.TryParse(currentValueStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out currentValue))
				{
					currentValue = defaultValue;

					//Only enforce keyword if feature is enabled
					if (Enabled(config))
					{
						config.SetKeyword(keyword, currentValue.ToString(CultureInfo.InvariantCulture));
					}
				}

				EditorGUI.BeginChangeCheck();
				var newValue = currentValue;
				newValue = Mathf.Clamp(EditorGUI.IntField(position, currentValue), min, max);
				if(EditorGUI.EndChangeCheck())
				{
					if(newValue != currentValue)
					{
						config.SetKeyword(keyword, newValue.ToString(CultureInfo.InvariantCulture));
					}
				}
			}
		}

		//----------------------------------------------------------------------------------------------------------------------------------------------------------------
		// DECORATORS

		internal class UIFeature_Separator : UIFeature
		{
			internal UIFeature_Separator() : base(null)
			{
				customGUI = true;
			}

			protected override void DrawGUI(Rect position, Config config, bool labelClicked)
			{
				TCP2_GUI.SeparatorSimple();
			}
		}

		internal class UIFeature_Space : UIFeature
		{
			float space = 8f;

			internal UIFeature_Space(List<KeyValuePair<string, string>> list) : base(list)
			{
				customGUI = true;
			}

			protected override void ProcessProperty(string key, string value)
			{
				if(key == "space")
					space = float.Parse(value, CultureInfo.InvariantCulture);
				else
					base.ProcessProperty(key, value);
			}

			protected override void DrawGUI(Rect position, Config config, bool labelClicked)
			{
				if(Enabled(config))
					GUILayout.Space(space);
			}
		}

		internal class UIFeature_SubHeader : UIFeature
		{
			internal UIFeature_SubHeader(List<KeyValuePair<string, string>> list) : base(list)
			{
				customGUI = true;
			}

			protected override void DrawGUI(Rect position, Config config, bool labelClicked)
			{
				if (this.helpTopic != null)
				{
					EditorGUILayout.BeginHorizontal();
					{
						TCP2_GUI.HelpButtonSG2(this.helpTopic);
						TCP2_GUI.SubHeaderGray(label);
					}
					EditorGUILayout.EndHorizontal();
				}
				else
				{
					TCP2_GUI.SubHeaderGray(label);
				}
			}
		}

		internal class UIFeature_Header : UIFeature
		{
			internal UIFeature_Header(List<KeyValuePair<string, string>> list) : base(list)
			{
				customGUI = true;
			}

			protected override void DrawGUI(Rect position, Config config, bool labelClicked)
			{
				TCP2_GUI.Header(label);
			}
		}

		internal class UIFeature_Warning : UIFeature
		{
			MessageType msgType = MessageType.Warning;

			internal UIFeature_Warning(List<KeyValuePair<string, string>> list) : base(list)
			{
				customGUI = true;
			}

			protected override void ProcessProperty(string key, string value)
			{
				if(key == "msgType")
					msgType = (MessageType)Enum.Parse(typeof(MessageType), value, true);
				else
					base.ProcessProperty(key, value);
			}

			protected override void DrawGUI(Rect position, Config config, bool labelClicked)
			{
				if(Enabled(config))
				{
					//EditorGUILayout.HelpBox(this.label, msgType);
					TCP2_GUI.HelpBoxLayout(label, msgType);
				}
			}
		}

		internal class UIFeature_DropDownStart : UIFeature
		{
			static List<UIFeature_DropDownStart> AllDropDowns = new List<UIFeature_DropDownStart>();
			internal static void ClearDropDownsList()
			{
				AllDropDowns.Clear();
			}

			public bool foldout;
			public GUIContent guiContent = GUIContent.none;

			internal UIFeature_DropDownStart(List<KeyValuePair<string, string>> list) : base(list)
			{
				customGUI = true;
				ignoreVisibility = true;

				if(list != null)
				{
					foreach(var kvp in list)
					{
						if(kvp.Key == "lbl")
						{
							guiContent = new GUIContent(kvp.Value.Trim('"'));
						}
					}
				}

				foldout = ProjectOptions.data.OpenedFoldouts.Contains(guiContent.text);

				AllDropDowns.Add(this);
			}

			protected override void DrawGUI(Rect position, Config config, bool labelClicked)
			{
				//Check if any feature within that Foldout are enabled, and show different color if so
				var hasToggledFeatures = false;
				var i = Array.IndexOf(Template.CurrentTemplate.uiFeatures, this);
				if(i >= 0)
				{
					for(i++; i < Template.CurrentTemplate.uiFeatures.Length; i++)
					{
						var uiFeature = Template.CurrentTemplate.uiFeatures[i];
						if(uiFeature is UIFeature_DropDownEnd)
							break;

						hasToggledFeatures |= uiFeature.Highlighted(config) && uiFeature.Enabled(config);
					}
				}

				var color = GUI.color;
				GUI.color *= EditorGUIUtility.isProSkin ? Color.white : new Color(.95f, .95f, .95f, 1f);
				EditorGUILayout.BeginVertical(EditorStyles.helpBox);
				GUI.color = color;
				EditorGUI.BeginChangeCheck();
				{
					var rect = GUILayoutUtility.GetRect(EditorGUIUtility.fieldWidth, EditorGUIUtility.fieldWidth, EditorGUIUtility.singleLineHeight, EditorGUIUtility.singleLineHeight, TCP2_GUI.HeaderDropDownBold);

					// hover
					TCP2_GUI.DrawHoverRect(rect);

					foldout = TCP2_GUI.HeaderFoldoutHighlight(rect, foldout, guiContent, hasToggledFeatures);
					FoldoutStack.Push(foldout);
				}
				if(EditorGUI.EndChangeCheck())
				{
					UpdatePersistentState();

					if(Event.current.alt || Event.current.control)
					{
						var state = foldout;
						foreach(var dd in AllDropDowns)
						{
							dd.foldout = state;
							dd.UpdatePersistentState();
						}
					}
				}
			}

			public void UpdatePersistentState()
			{
				if(foldout && !ProjectOptions.data.OpenedFoldouts.Contains(guiContent.text))
					ProjectOptions.data.OpenedFoldouts.Add(guiContent.text);
				else if(!foldout && ProjectOptions.data.OpenedFoldouts.Contains(guiContent.text))
					ProjectOptions.data.OpenedFoldouts.Remove(guiContent.text);
			}
		}

		internal class UIFeature_DropDownEnd : UIFeature
		{
			internal UIFeature_DropDownEnd() : base(null)
			{
				customGUI = true;
				ignoreVisibility = true;
			}

			protected override void DrawGUI(Rect position, Config config, bool labelClicked)
			{
				FoldoutStack.Pop();

				EditorGUILayout.EndVertical();
			}
		}
	}
}
