//
// System.Web.UI.ControlBuilder.cs
//
// Authors:
// 	Duncan Mak  (duncan@ximian.com)
// 	Gonzalo Paniagua Javier (gonzalo@ximian.com)
//
// (C) 2002, 2003 Ximian, Inc. (http://www.ximian.com)
// Copyright (C) 2005 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

using System.Collections;
using System.Configuration;
using System.CodeDom;
using System.Reflection;
using System.Security.Permissions;
using System.Web.Compilation;
using System.Web.Configuration;
using System.IO;
using System.Web.UI.WebControls;

namespace System.Web.UI {

	// CAS
	[AspNetHostingPermission (SecurityAction.LinkDemand, Level = AspNetHostingPermissionLevel.Minimal)]
	[AspNetHostingPermission (SecurityAction.InheritanceDemand, Level = AspNetHostingPermissionLevel.Minimal)]
	public class ControlBuilder
	{
		internal static BindingFlags flagsNoCase = BindingFlags.Public |
			BindingFlags.Instance |
			BindingFlags.Static |
			BindingFlags.IgnoreCase;

		ControlBuilder myNamingContainer;
		TemplateParser parser;
		internal ControlBuilder parentBuilder;
		Type type;	       
		string tagName;
		string id;
		internal IDictionary attribs;
		internal int line;
		internal string fileName;
		bool childrenAsProperties;
		bool isIParserAccessor = true;
		bool hasAspCode;
		internal ControlBuilder defaultPropertyBuilder;
		ArrayList children;
		static int nextID;

		internal bool haveParserVariable;
		internal CodeMemberMethod method;
		internal CodeStatementCollection methodStatements;
		internal CodeMemberMethod renderMethod;
		internal int renderIndex;
		internal bool isProperty;
		internal ILocation location;
		ArrayList otherTags;
		
		public ControlBuilder ()
		{
		}

		internal ControlBuilder (TemplateParser parser,
					 ControlBuilder parentBuilder,
					 Type type,
					 string tagName,
					 string id,
					 IDictionary attribs,
					 int line,
					 string sourceFileName)

		{
			this.parser = parser;
			this.parentBuilder = parentBuilder;
			this.type = type;
			this.tagName = tagName;
			this.id = id;
			this.attribs = attribs;
			this.line = line;
			this.fileName = sourceFileName;
		}

		internal void EnsureOtherTags ()
		{
			if (otherTags == null)
				otherTags = new ArrayList ();
		}
		
		internal ArrayList OtherTags {
			get { return otherTags; }
		}

		public Type ControlType {
			get { return type; }
		}

		protected bool FChildrenAsProperties {
			get { return childrenAsProperties; }
		}

		protected bool FIsNonParserAccessor {
			get { return !isIParserAccessor; }
		}

		public bool HasAspCode {
			get { return hasAspCode; }
		}

		public string ID {
			get { return id; }
			set { id = value; }
		}

		internal ArrayList Children {
			get { return children; }
		}

		internal void SetControlType (Type t)
		{
			type = t;
		}
		
		protected bool InDesigner {
			get { return false; }
		}

		public Type NamingContainerType {
			get {
				ControlBuilder cb = myNamingContainer;
				
				if (cb == null)
					return typeof (Control);

				return cb.ControlType;
			}
		}

		ControlBuilder MyNamingContainer {
			get {
				if (myNamingContainer == null) {
					Type controlType = parentBuilder != null ? parentBuilder.ControlType : null;
					
					if (parentBuilder == null && controlType == null)
						myNamingContainer = null;
					else if (parentBuilder is TemplateBuilder)
						myNamingContainer = parentBuilder;
					else if (controlType != null && typeof (INamingContainer).IsAssignableFrom (controlType))
						myNamingContainer = parentBuilder;
					else
						myNamingContainer = parentBuilder.MyNamingContainer;
				}

				return myNamingContainer;
			}
		}
			
#if NET_2_0
		public virtual
#else
		internal
#endif
		Type BindingContainerType {
			get {
				ControlBuilder cb = MyNamingContainer;
				if (cb == null)
					return typeof (Control);

#if NET_2_0
				if (cb is ContentBuilderInternal)
					return cb.BindingContainerType;
#endif

				if (cb is TemplateBuilder) {
					Type ct =((TemplateBuilder) cb).ContainerType;
					if (ct == null)
						return typeof (Control);
					return ct;
				}

				return cb.ControlType;
			}
		}

		internal TemplateBuilder ParentTemplateBuilder {
			get {
				if (parentBuilder == null)
					return null;
				else if (parentBuilder is TemplateBuilder)
					return (TemplateBuilder) parentBuilder;
				else
					return parentBuilder.ParentTemplateBuilder;
			}
		}

		protected TemplateParser Parser {
			get { return parser; }
		}

		public string TagName {
			get { return tagName; }
		}

		internal RootBuilder Root {
			get {
				if (GetType () == typeof (RootBuilder))
					return (RootBuilder) this;

				return (RootBuilder) parentBuilder.Root;
			}
		}

		internal bool ChildrenAsProperties {
			get { return childrenAsProperties; }
		}
		
		public virtual bool AllowWhitespaceLiterals ()
		{
			return true;
		}

		public virtual void AppendLiteralString (string s)
		{
			if (s == null || s.Length == 0)
				return;

			if (childrenAsProperties || !isIParserAccessor) {
				if (defaultPropertyBuilder != null) {
					defaultPropertyBuilder.AppendLiteralString (s);
				} else if (s.Trim ().Length != 0) {
					throw new HttpException (String.Format ("Literal content not allowed for '{0}' {1} \"{2}\"",
										tagName, GetType (), s));
				}

				return;
			}
			
			if (!AllowWhitespaceLiterals () && s.Trim ().Length == 0)
				return;

			if (HtmlDecodeLiterals ())
				s = HttpUtility.HtmlDecode (s);

			if (children == null)
				children = new ArrayList ();

			children.Add (s);
		}

		public virtual void AppendSubBuilder (ControlBuilder subBuilder)
		{
			subBuilder.OnAppendToParentBuilder (this);
			
			subBuilder.parentBuilder = this;
			if (childrenAsProperties) {
				AppendToProperty (subBuilder);
				return;
			}

			if (typeof (CodeRenderBuilder).IsAssignableFrom (subBuilder.GetType ())) {
				AppendCode (subBuilder);
				return;
			}

			if (children == null)
				children = new ArrayList ();

			children.Add (subBuilder);
		}

		void AppendToProperty (ControlBuilder subBuilder)
		{
			if (typeof (CodeRenderBuilder) == subBuilder.GetType ())
				throw new HttpException ("Code render not supported here.");

			if (defaultPropertyBuilder != null) {
				defaultPropertyBuilder.AppendSubBuilder (subBuilder);
				return;
			}

			if (children == null)
				children = new ArrayList ();

			children.Add (subBuilder);
		}

		void AppendCode (ControlBuilder subBuilder)
		{
			if (type != null && !(typeof (Control).IsAssignableFrom (type)))
				throw new HttpException ("Code render not supported here.");

			if (typeof (CodeRenderBuilder) == subBuilder.GetType ())
				hasAspCode = true;

			if (children == null)
				children = new ArrayList ();

			children.Add (subBuilder);
		}

		public virtual void CloseControl ()
		{
		}

#if NET_2_0		
		static Type MapTagType (Type tagType)
		{
			if (tagType == null)
				return null;
			
			PagesSection ps = WebConfigurationManager.GetSection ("system.web/pages") as PagesSection;
			if (ps == null)
				return tagType;

			TagMapCollection tags = ps.TagMapping;
			if (tags == null || tags.Count == 0)
				return tagType;
			
			string tagTypeName = tagType.ToString ();
			Type mappedType, originalType;
			string originalTypeName = String.Empty, mappedTypeName = String.Empty;
			bool missingType;
			Exception error;
			
			foreach (TagMapInfo tmi in tags) {
				error = null;
				originalType = null;
				
				try {
					originalTypeName = tmi.TagType;
					originalType = HttpApplication.LoadType (originalTypeName);
					if (originalType == null)
						missingType = true;
					else
						missingType = false;
				} catch (Exception ex) {
					missingType = true;
					error = ex;
				}
				if (missingType)
					throw new HttpException (String.Format ("Could not load type {0}", originalTypeName), error);
				
				if (originalTypeName == tagTypeName) {
					mappedTypeName = tmi.MappedTagType;
					error = null;
					mappedType = null;
					
					try {
						mappedType = HttpApplication.LoadType (mappedTypeName);
						if (mappedType == null)
							missingType = true;
						else
							missingType = false;
					} catch (Exception ex) {
						missingType = true;
						error = ex;
					}

					if (missingType)
						throw new HttpException (String.Format ("Could not load type {0}", mappedTypeName),
									 error);
					
					if (!mappedType.IsSubclassOf (originalType))
						throw new ConfigurationErrorsException (
							String.Format ("The specified type '{0}' used for mapping must inherit from the original type '{1}'.", mappedTypeName, originalTypeName));

					return mappedType;
				}
			}
			
			return tagType;
		}
#endif

		public static ControlBuilder CreateBuilderFromType (TemplateParser parser,
								    ControlBuilder parentBuilder,
								    Type type,
								    string tagName,
								    string id,
								    IDictionary attribs,
								    int line,
								    string sourceFileName)
		{

			Type tagType;
#if NET_2_0
			tagType = MapTagType (type);
#else
			tagType = type;
#endif
			ControlBuilder  builder;
			object [] atts = tagType.GetCustomAttributes (typeof (ControlBuilderAttribute), true);
			if (atts != null && atts.Length > 0) {
				ControlBuilderAttribute att = (ControlBuilderAttribute) atts [0];
				builder = (ControlBuilder) Activator.CreateInstance (att.BuilderType);
			} else {
				builder = new ControlBuilder ();
			}

			builder.Init (parser, parentBuilder, tagType, tagName, id, attribs);
			builder.line = line;
			builder.fileName = sourceFileName;
			return builder;
		}

		public virtual Type GetChildControlType (string tagName, IDictionary attribs)
		{
			return null;
		}

		public virtual bool HasBody ()
		{
			return true;
		}

		public virtual bool HtmlDecodeLiterals ()
		{
			return false;
		}

		ControlBuilder CreatePropertyBuilder (string propName, TemplateParser parser, IDictionary atts)
		{
			PropertyInfo prop = type.GetProperty (propName, flagsNoCase);
			if (prop == null) {
				string msg = String.Format ("Property {0} not found in type {1}", propName, type);
				throw new HttpException (msg);
			}

			Type propType = prop.PropertyType;
			ControlBuilder builder = null;
			if (typeof (ICollection).IsAssignableFrom (propType)) {
				builder = new CollectionBuilder ();
			} else if (typeof (ITemplate).IsAssignableFrom (propType)) {
				builder = new TemplateBuilder (prop);
			} else if (typeof (string) == propType) {
				builder = new StringPropertyBuilder (prop.Name);
			} else {
				builder = CreateBuilderFromType (parser, parentBuilder, propType, prop.Name,
								 null, atts, line, fileName);
				builder.isProperty = true;
				return builder;
			}

			builder.Init (parser, this, null, prop.Name, null, atts);
			builder.fileName = fileName;
			builder.line = line;
			builder.isProperty = true;
			return builder;
		}
		
		public virtual void Init (TemplateParser parser,
					  ControlBuilder parentBuilder,
					  Type type,
					  string tagName,
					  string id,
					  IDictionary attribs)
		{
			this.parser = parser;
			if (parser != null)
				this.location = parser.Location;

			this.parentBuilder = parentBuilder;
			this.type = type;
			this.tagName = tagName;
			this.id = id;
			this.attribs = attribs;
			if (type == null)
				return;

			if (this is TemplateBuilder)
				return;

			object [] atts = type.GetCustomAttributes (typeof (ParseChildrenAttribute), true);
			
			if (!typeof (IParserAccessor).IsAssignableFrom (type) && atts.Length == 0) {
				isIParserAccessor = false;
				childrenAsProperties = true;
			} else if (atts.Length > 0) {
				ParseChildrenAttribute att = (ParseChildrenAttribute) atts [0];
				childrenAsProperties = att.ChildrenAsProperties;
				if (childrenAsProperties && att.DefaultProperty.Length != 0)
					defaultPropertyBuilder = CreatePropertyBuilder (att.DefaultProperty,
											parser, null);
			}
		}

		public virtual bool NeedsTagInnerText ()
		{
			return false;
		}

		public virtual void OnAppendToParentBuilder (ControlBuilder parentBuilder)
		{
			if (defaultPropertyBuilder == null)
				return;

			ControlBuilder old = defaultPropertyBuilder;
			defaultPropertyBuilder = null;
			AppendSubBuilder (old);
		}

		internal void SetTagName (string name)
		{
			tagName = name;
		}
		
		public virtual void SetTagInnerText (string text)
		{
		}

		internal string GetNextID (string proposedID)
		{
			if (proposedID != null && proposedID.Trim ().Length != 0)
				return proposedID;

			return "_bctrl_" + nextID++;
		}

		internal virtual ControlBuilder CreateSubBuilder (string tagid,
								  Hashtable atts,
								  Type childType,
								  TemplateParser parser,
								  ILocation location)
		{
			ControlBuilder childBuilder = null;
			if (childrenAsProperties) {
				if (defaultPropertyBuilder == null)
					childBuilder = CreatePropertyBuilder (tagid, parser, atts);
				else {
					if (defaultPropertyBuilder.TagName == tagid) {
						// The child tag is the same what our default property name. Act as if there was
						// no default property builder, or otherwise we'll end up with invalid nested
						// builder call.
						defaultPropertyBuilder = null;
						childBuilder = CreatePropertyBuilder (tagid, parser, atts);
					} else
						childBuilder = defaultPropertyBuilder.CreateSubBuilder (tagid, atts,
													null, parser,
													location);
				}
				return childBuilder;
			}

			if (tagName == tagid)
				return null;
			
			childType = GetChildControlType (tagid, atts);
			if (childType == null)
				return null;

			childBuilder = CreateBuilderFromType (parser, this, childType, tagid, id, atts,
							      location.BeginLine, location.Filename);

			return childBuilder;
		}

		internal virtual object CreateInstance ()
		{
			// HtmlGenericControl, HtmlTableCell...
			object [] atts = type.GetCustomAttributes (typeof (ConstructorNeedsTagAttribute), true);
			object [] args = null;
			if (atts != null && atts.Length > 0) {
				ConstructorNeedsTagAttribute att = (ConstructorNeedsTagAttribute) atts [0];
				if (att.NeedsTag)
					args = new object [] {tagName};
			}

			return Activator.CreateInstance (type, args);
		}

		internal virtual void CreateChildren (object parent) 
		{
			if (children == null || children.Count == 0)
				return;

			IParserAccessor parser = parent as IParserAccessor;
			if (parser == null)
				return;

			foreach (object o in children) {
				if (o is string) {
					parser.AddParsedSubObject (new LiteralControl ((string) o));
				} else {
					parser.AddParsedSubObject (((ControlBuilder) o).CreateInstance ());
				}
			}
		}
#if NET_2_0
		[MonoTODO ("unsure, lack documentation")]
		public virtual object BuildObject ()
		{
			return CreateInstance ();
		}

		internal void ResetState()
		{
			renderIndex = 0;
			haveParserVariable = false;

			if (Children != null) {
				foreach (object child in Children) {
					ControlBuilder cb = child as ControlBuilder;
					if (cb != null)
						cb.ResetState ();
				}
			}
		}
#endif
	}
}
