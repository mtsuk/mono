﻿//
// dynamic.cs: support for dynamic expressions
//
// Authors: Marek Safar (marek.safar@gmail.com)
//
// Dual licensed under the terms of the MIT X11 or GNU GPL
//
// Copyright 2009 Novell, Inc
//

using System;
using System.Reflection.Emit;
using SLE = System.Linq.Expressions;

#if NET_4_0
using System.Dynamic;
#endif

namespace Mono.CSharp
{
	//
	// A copy of Microsoft.CSharp/Microsoft.CSharp.RuntimeBinder/CSharpBinderFlags.cs
	// has to be kept in sync
	//
	[Flags]
	public enum CSharpBinderFlags
	{
		None = 0,
		CheckedContext = 1,
		InvokeSimpleName = 1 << 1,
		InvokeSpecialName = 1 << 2,
		BinaryOperationLogical = 1 << 3,
		ConvertExplicit = 1 << 4,
		ConvertArrayIndex = 1 << 5,
		ResultIndexed = 1 << 6,
		ValueFromCompoundAssignment = 1 << 7,
		ResultDiscarded = 1 << 8
	}

	//
	// Type expression with internal dynamic type symbol
	//
	class DynamicTypeExpr : TypeExpr
	{
		public DynamicTypeExpr (Location loc)
		{
			this.loc = loc;

			type = InternalType.Dynamic;
			eclass = ExprClass.Type;
		}

		public override bool CheckAccessLevel (IMemberContext ds)
		{
			return true;
		}

		protected override TypeExpr DoResolveAsTypeStep (IMemberContext ec)
		{
			return this;
		}
	}

	#region Dynamic runtime binder expressions

	//
	// Expression created from runtime dynamic object value by dynamic binder
	//
	public class RuntimeValueExpression : Expression, IDynamicAssign, IMemoryLocation
	{
#if !NET_4_0
		public class DynamicMetaObject
		{
			public Type RuntimeType;
			public Type LimitType;
			public SLE.Expression Expression;
		}
#endif

		readonly DynamicMetaObject obj;

		public RuntimeValueExpression (DynamicMetaObject obj, bool isCompileTimeType)
		{
			this.obj = obj;
			this.type = isCompileTimeType ? obj.LimitType : obj.RuntimeType;
			this.type = obj.LimitType;
			this.eclass = ExprClass.Variable;
		}

		public void AddressOf (EmitContext ec, AddressOp mode)
		{
			throw new NotImplementedException ();
		}

		public override Expression CreateExpressionTree (ResolveContext ec)
		{
			throw new NotImplementedException ();
		}

		protected override Expression DoResolve (ResolveContext ec)
		{
			return this;
		}

		public override Expression DoResolveLValue (ResolveContext ec, Expression right_side)
		{
			return this;
		}

		public override void Emit (EmitContext ec)
		{
			throw new NotImplementedException ();
		}

		#region IAssignMethod Members

		public void Emit (EmitContext ec, bool leave_copy)
		{
			throw new NotImplementedException ();
		}

		public void EmitAssign (EmitContext ec, Expression source, bool leave_copy, bool prepare_for_load)
		{
			throw new NotImplementedException ();
		}

		#endregion

		public SLE.Expression MakeAssignExpression (BuilderContext ctx)
		{
			return obj.Expression;
		}

		public override SLE.Expression MakeExpression (BuilderContext ctx)
		{
			return SLE.Expression.Convert (obj.Expression, type);
		}

		public DynamicMetaObject MetaObject {
			get { return obj; }
		}
	}

	//
	// Wraps runtime dynamic expression into expected type. Needed
	// to satify expected type check by dynamic binder and no conversion
	// is required (ResultDiscarded).
	//
	public class DynamicResultCast : ShimExpression
	{
		public DynamicResultCast (Type type, Expression expr)
			: base (expr)
		{
			this.type = type;
		}

		protected override Expression DoResolve (ResolveContext ec)
		{
			expr = expr.Resolve (ec);
			eclass = ExprClass.Value;
			return this;
		}

#if NET_4_0
		public override SLE.Expression MakeExpression (BuilderContext ctx)
		{
			return SLE.Expression.Block (expr.MakeExpression (ctx),	SLE.Expression.Default (type));
		}
#endif
	}

	#endregion

	//
	// Creates dynamic binder expression
	//
	interface IDynamicBinder
	{
		Expression CreateCallSiteBinder (ResolveContext ec, Arguments args);
	}

	//
	// Extends standard assignment interface for expressions
	// supported by dynamic resolver
	//
	interface IDynamicAssign : IAssignMethod
	{
#if NET_4_0
		SLE.Expression MakeAssignExpression (BuilderContext ctx);
#endif
	}

	//
	// Base dynamic expression statement creator
	//
	class DynamicExpressionStatement : ExpressionStatement
	{
		class StaticDataClass : CompilerGeneratedClass
		{
			public StaticDataClass ()
				: base (new RootDeclSpace (new NamespaceEntry (null, null, null)),
					new MemberName (CompilerGeneratedClass.MakeName (null, "c", "DynamicSites", 0)),
					Modifiers.INTERNAL | Modifiers.STATIC)
			{
				ModFlags &= ~Modifiers.SEALED;
			}
		}

		//
		// Binder flag dynamic constant, the value is combination of
		// flags known at resolve stage and flags known only at emit
		// stage
		//
		protected class BinderFlags : EnumConstant
		{
			DynamicExpressionStatement statement;
			CSharpBinderFlags flags;

			public BinderFlags (CSharpBinderFlags flags, DynamicExpressionStatement statement)
				: base (statement.loc)
			{
				this.flags = flags;
				this.statement = statement;
			}

			protected override Expression DoResolve (ResolveContext ec)
			{
				Child = new IntConstant ((int) (flags | statement.flags), statement.loc).Resolve (ec);

				type = TypeManager.binder_flags;
				eclass = Child.eclass;
				return this;
			}
		}

		static StaticDataClass global_site_container;
		static int field_counter;
		static int container_counter;

		readonly Arguments arguments;
		protected IDynamicBinder binder;
		protected Expression binder_expr;

		// Used by BinderFlags
		protected CSharpBinderFlags flags;

		public DynamicExpressionStatement (IDynamicBinder binder, Arguments args, Location loc)
		{
			this.binder = binder;
			this.arguments = args;
			this.loc = loc;
		}

		public Arguments Arguments {
			get {
				return arguments;
			}
		}

		static TypeContainer CreateSiteContainer ()
		{
			if (global_site_container == null) {
				global_site_container = new StaticDataClass ();
				RootContext.ToplevelTypes.AddCompilerGeneratedClass (global_site_container);
				global_site_container.DefineType ();
				global_site_container.Define ();
//				global_site_container.EmitType ();

				RootContext.RegisterCompilerGeneratedType (global_site_container.TypeBuilder);
			}

			return global_site_container;
		}

		static Field CreateSiteField (FullNamedExpression type)
		{
			TypeContainer site_container = CreateSiteContainer ();
			Field f = new Field (site_container, type, Modifiers.PUBLIC | Modifiers.STATIC,
				new MemberName ("Site" +  field_counter++), null);
			f.Define ();

			site_container.AddField (f);
			return f;
		}

		public override Expression CreateExpressionTree (ResolveContext ec)
		{
			ec.Report.Error (1963, loc, "An expression tree cannot contain a dynamic operation");
			return null;
		}

		protected override Expression DoResolve (ResolveContext ec)
		{
			if (DoResolveCore (ec))
				binder_expr = binder.CreateCallSiteBinder (ec, arguments);

			return this;
		}

		protected bool DoResolveCore (ResolveContext rc)
		{
			int errors = rc.Report.Errors;

			if (TypeManager.binder_type == null) {
				var t = TypeManager.CoreLookupType (rc.Compiler,
					"Microsoft.CSharp.RuntimeBinder", "Binder", MemberKind.Class, true);
				if (t != null)
					TypeManager.binder_type = new TypeExpression (t, Location.Null);
			}

			if (TypeManager.call_site_type == null)
				TypeManager.call_site_type = TypeManager.CoreLookupType (rc.Compiler,
					"System.Runtime.CompilerServices", "CallSite", MemberKind.Class, true);

			if (TypeManager.generic_call_site_type == null)
				TypeManager.generic_call_site_type = TypeManager.CoreLookupType (rc.Compiler,
					"System.Runtime.CompilerServices", "CallSite`1", MemberKind.Class, true);

			if (TypeManager.binder_flags == null) {
				TypeManager.binder_flags = TypeManager.CoreLookupType (rc.Compiler,
					"Microsoft.CSharp.RuntimeBinder", "CSharpBinderFlags", MemberKind.Enum, true);
			}

			eclass = ExprClass.Value;

			if (type == null)
				type = InternalType.Dynamic;

			if (rc.Report.Errors == errors)
				return true;

			rc.Report.Error (1969, loc,
				"Dynamic operation cannot be compiled without `Microsoft.CSharp.dll' assembly reference");
			return false;
		}

		public override void Emit (EmitContext ec)
		{
			EmitCall (ec, binder_expr, arguments,  false);
		}

		public override void EmitStatement (EmitContext ec)
		{
			EmitCall (ec, binder_expr, arguments, true);
		}

		protected void EmitCall (EmitContext ec, Expression binder, Arguments arguments, bool isStatement)
		{
			int dyn_args_count = arguments == null ? 0 : arguments.Count;
			TypeExpr site_type = CreateSiteType (RootContext.ToplevelTypes.Compiler, arguments, dyn_args_count, isStatement);
			FieldExpr site_field_expr = new FieldExpr (CreateSiteField (site_type), loc);

			SymbolWriter.OpenCompilerGeneratedBlock (ec.ig);

			Arguments args = new Arguments (1);
			args.Add (new Argument (binder));
			StatementExpression s = new StatementExpression (new SimpleAssign (site_field_expr, new Invocation (new MemberAccess (site_type, "Create"), args)));
			
			BlockContext bc = new BlockContext (ec.MemberContext, null, TypeManager.void_type);		
			if (s.Resolve (bc)) {
				Statement init = new If (new Binary (Binary.Operator.Equality, site_field_expr, new NullLiteral (loc), loc), s, loc);
				init.Emit (ec);
			}

			args = new Arguments (1 + dyn_args_count);
			args.Add (new Argument (site_field_expr));
			if (arguments != null) {
				foreach (Argument a in arguments) {
					if (a is NamedArgument) {
						// Name is not valid in this context
						args.Add (new Argument (a.Expr, a.ArgType));
						continue;
					}

					args.Add (a);
				}
			}

			Expression target = new DelegateInvocation (new MemberAccess (site_field_expr, "Target", loc).Resolve (bc), args, loc).Resolve (bc);
			if (target != null)
				target.Emit (ec);

			SymbolWriter.CloseCompilerGeneratedBlock (ec.ig);
		}

		public static MemberAccess GetBinderNamespace (Location loc)
		{
			return new MemberAccess (new MemberAccess (
				new QualifiedAliasMember (QualifiedAliasMember.GlobalAlias, "Microsoft", loc), "CSharp", loc), "RuntimeBinder", loc);
		}

		public static MemberAccess GetBinder (string name, Location loc)
		{
			return new MemberAccess (TypeManager.binder_type, name, loc);
		}

		TypeExpr CreateSiteType (CompilerContext ctx, Arguments arguments, int dyn_args_count, bool is_statement)
		{
			int default_args = is_statement ? 1 : 2;

			bool has_ref_out_argument = false;
			FullNamedExpression[] targs = new FullNamedExpression[dyn_args_count + default_args];
			targs [0] = new TypeExpression (TypeManager.call_site_type, loc);
			for (int i = 0; i < dyn_args_count; ++i) {
				Type arg_type;
				Argument a = arguments [i];
				if (a.Type == TypeManager.null_type)
					arg_type = TypeManager.object_type;
				else
					arg_type = a.Type;

				if (a.ArgType == Argument.AType.Out || a.ArgType == Argument.AType.Ref)
					has_ref_out_argument = true;

				targs [i + 1] = new TypeExpression (arg_type, loc);
			}

			TypeExpr del_type = null;
			if (!has_ref_out_argument) {
				string d_name = is_statement ? "Action`" : "Func`";

				Type t = TypeManager.CoreLookupType (ctx, "System", d_name + (dyn_args_count + default_args), MemberKind.Delegate, false);
				if (t != null) {
					if (!is_statement)
						targs [targs.Length - 1] = new TypeExpression (type, loc);

					del_type = new GenericTypeExpr (t, new TypeArguments (targs), loc);
				}
			}

			//
			// Create custom delegate when no appropriate predefined one is found
			//
			if (del_type == null) {
				Type rt = is_statement ? TypeManager.void_type : type;
				Parameter[] p = new Parameter [dyn_args_count + 1];
				p[0] = new Parameter (targs [0], "p0", Parameter.Modifier.NONE, null, loc);

				for (int i = 1; i < dyn_args_count + 1; ++i)
					p[i] = new Parameter (targs[i], "p" + i.ToString ("X"), arguments[i - 1].Modifier, null, loc);

				TypeContainer parent = CreateSiteContainer ();
				Delegate d = new Delegate (parent.NamespaceEntry, parent, new TypeExpression (rt, loc),
					Modifiers.INTERNAL | Modifiers.COMPILER_GENERATED,
					new MemberName ("Container" + container_counter++.ToString ("X")),
					new ParametersCompiled (ctx, p), null);

				d.DefineType ();
				d.Define ();
				d.Emit ();

				parent.AddDelegate (d);
				del_type = new TypeExpression (d.TypeBuilder, loc);
			}

			TypeExpr site_type = new GenericTypeExpr (TypeManager.generic_call_site_type, new TypeArguments (del_type), loc);
			return site_type;
		}

		public static void Reset ()
		{
			global_site_container = null;
			field_counter = container_counter = 0;
		}
	}

	//
	// Dynamic member access compound assignment for events
	//
	class DynamicEventCompoundAssign : DynamicExpressionStatement, IDynamicBinder
	{
		string name;
		Statement condition;

		public DynamicEventCompoundAssign (string name, Arguments args, ExpressionStatement assignment, ExpressionStatement invoke, Location loc)
			: base (null, args, loc)
		{
			this.name = name;
			base.binder = this;

			// Used by += or -= only
			type = TypeManager.bool_type;

			condition = new If (
				new Binary (Binary.Operator.Equality, this, new BoolLiteral (true, loc), loc),
				new StatementExpression (invoke), new StatementExpression (assignment),
				loc);
		}

		public Expression CreateCallSiteBinder (ResolveContext ec, Arguments args)
		{
			Arguments binder_args = new Arguments (3);

			binder_args.Add (new Argument (new BinderFlags (0, this)));
			binder_args.Add (new Argument (new StringLiteral (name, loc)));
			binder_args.Add (new Argument (new TypeOf (new TypeExpression (ec.CurrentType, loc), loc)));

			return new Invocation (GetBinder ("IsEvent", loc), binder_args);
		}

		public override void EmitStatement (EmitContext ec)
		{
			condition.Emit (ec);
		}
	}

	class DynamicConversion : DynamicExpressionStatement, IDynamicBinder
	{
		public DynamicConversion (Type targetType, CSharpBinderFlags flags, Arguments args, Location loc)
			: base (null, args, loc)
		{
			type = targetType;
			base.flags = flags;
			base.binder = this;
		}

		public Expression CreateCallSiteBinder (ResolveContext ec, Arguments args)
		{
			Arguments binder_args = new Arguments (3);

			flags |= ec.HasSet (ResolveContext.Options.CheckedScope) ? CSharpBinderFlags.CheckedContext : 0;

			binder_args.Add (new Argument (new BinderFlags (flags, this)));
			binder_args.Add (new Argument (new TypeOf (new TypeExpression (ec.CurrentType, loc), loc)));						
			binder_args.Add (new Argument (new TypeOf (new TypeExpression (type, loc), loc)));
			return new Invocation (GetBinder ("Convert", loc), binder_args);
		}
	}

	class DynamicConstructorBinder : DynamicExpressionStatement, IDynamicBinder
	{
		public DynamicConstructorBinder (Type type, Arguments args, Location loc)
			: base (null, args, loc)
		{
			this.type = type;
			base.binder = this;
		}

		public Expression CreateCallSiteBinder (ResolveContext ec, Arguments args)
		{
			Arguments binder_args = new Arguments (3);

			binder_args.Add (new Argument (new BinderFlags (0, this)));
			binder_args.Add (new Argument (new TypeOf (new TypeExpression (ec.CurrentType, loc), loc)));
			binder_args.Add (new Argument (new ImplicitlyTypedArrayCreation ("[]", args.CreateDynamicBinderArguments (ec), loc)));

			return new Invocation (GetBinder ("InvokeConstructor", loc), binder_args);
		}
	}

	class DynamicIndexBinder : DynamicMemberAssignable
	{
		public DynamicIndexBinder (Arguments args, Location loc)
			: base (args, loc)
		{
		}

		protected override Expression CreateCallSiteBinder (ResolveContext ec, Arguments args, bool isSet)
		{
			Arguments binder_args = new Arguments (3);

			binder_args.Add (new Argument (new BinderFlags (0, this)));
			binder_args.Add (new Argument (new TypeOf (new TypeExpression (ec.CurrentType, loc), loc)));
			binder_args.Add (new Argument (new ImplicitlyTypedArrayCreation ("[]", args.CreateDynamicBinderArguments (ec), loc)));

			return new Invocation (GetBinder (isSet ? "SetIndex" : "GetIndex", loc), binder_args);
		}
	}

	class DynamicInvocation : DynamicExpressionStatement, IDynamicBinder
	{
		ATypeNameExpression member;

		public DynamicInvocation (ATypeNameExpression member, Arguments args, Location loc)
			: base (null, args, loc)
		{
			base.binder = this;
			this.member = member;
		}

		public DynamicInvocation (ATypeNameExpression member, Arguments args, Type type, Location loc)
			: this (member, args, loc)
		{
			// When a return type is known not to be dynamic
			this.type = type;
		}

		public static DynamicInvocation CreateSpecialNameInvoke (ATypeNameExpression member, Arguments args, Location loc)
		{
			return new DynamicInvocation (member, args, loc) {
				flags = CSharpBinderFlags.InvokeSpecialName
			};
		}

		public Expression CreateCallSiteBinder (ResolveContext ec, Arguments args)
		{
			Arguments binder_args = new Arguments (member != null ? 5 : 3);
			bool is_member_access = member is MemberAccess;

			CSharpBinderFlags call_flags;
			if (!is_member_access && member is SimpleName) {
				call_flags = CSharpBinderFlags.InvokeSimpleName;
				is_member_access = true;
			} else {
				call_flags = 0;
			}

			binder_args.Add (new Argument (new BinderFlags (call_flags, this)));

			if (is_member_access)
				binder_args.Add (new Argument (new StringLiteral (member.Name, member.Location)));

			if (member != null && member.HasTypeArguments) {
				TypeArguments ta = member.TypeArguments;
				if (ta.Resolve (ec)) {
					var targs = new ArrayInitializer (ta.Count, loc);
					foreach (Type t in ta.Arguments)
						targs.Add (new TypeOf (new TypeExpression (t, loc), loc));

					binder_args.Add (new Argument (new ImplicitlyTypedArrayCreation ("[]", targs, loc)));
				}
			} else if (is_member_access) {
				binder_args.Add (new Argument (new NullLiteral (loc)));
			}

			binder_args.Add (new Argument (new TypeOf (new TypeExpression (ec.CurrentType, loc), loc)));

			Expression real_args;
			if (args == null) {
				// Cannot be null because .NET trips over
				real_args = new ArrayCreation (
					new MemberAccess (GetBinderNamespace (loc), "CSharpArgumentInfo", loc), "[]",
					new ArrayInitializer (0, loc), loc);
			} else {
				real_args = new ImplicitlyTypedArrayCreation ("[]", args.CreateDynamicBinderArguments (ec), loc);
			}

			binder_args.Add (new Argument (real_args));

			return new Invocation (GetBinder (is_member_access ? "InvokeMember" : "Invoke", loc), binder_args);
		}

		public override void EmitStatement (EmitContext ec)
		{
			flags |= CSharpBinderFlags.ResultDiscarded;
			base.EmitStatement (ec);
		}
	}

	class DynamicMemberBinder : DynamicMemberAssignable
	{
		readonly string name;

		public DynamicMemberBinder (string name, Arguments args, Location loc)
			: base (args, loc)
		{
			this.name = name;
		}

		protected override Expression CreateCallSiteBinder (ResolveContext ec, Arguments args, bool isSet)
		{
			Arguments binder_args = new Arguments (4);

			binder_args.Add (new Argument (new BinderFlags (0, this)));
			binder_args.Add (new Argument (new StringLiteral (name, loc)));
			binder_args.Add (new Argument (new TypeOf (new TypeExpression (ec.CurrentType, loc), loc)));
			binder_args.Add (new Argument (new ImplicitlyTypedArrayCreation ("[]", args.CreateDynamicBinderArguments (ec), loc)));

			return new Invocation (GetBinder (isSet ? "SetMember" : "GetMember", loc), binder_args);
		}
	}

	//
	// Any member binder which can be source and target of assignment
	//
	abstract class DynamicMemberAssignable : DynamicExpressionStatement, IDynamicBinder, IAssignMethod
	{
		Expression setter;
		Arguments setter_args;

		protected DynamicMemberAssignable (Arguments args, Location loc)
			: base (null, args, loc)
		{
			base.binder = this;
		}

		public Expression CreateCallSiteBinder (ResolveContext ec, Arguments args)
		{
			//
			// DoResolve always uses getter
			//
			return CreateCallSiteBinder (ec, args, false);
		}

		protected abstract Expression CreateCallSiteBinder (ResolveContext ec, Arguments args, bool isSet);

		public override Expression DoResolveLValue (ResolveContext rc, Expression right_side)
		{
			if (right_side == EmptyExpression.OutAccess.Instance) {
				right_side.DoResolveLValue (rc, this);
				return null;
			}

			if (DoResolveCore (rc)) {
				setter_args = new Arguments (Arguments.Count + 1);
				setter_args.AddRange (Arguments);
				setter_args.Add (new Argument (right_side));
				setter = CreateCallSiteBinder (rc, setter_args, true);
			}

			eclass = ExprClass.Variable;
			return this;
		}

		public override void Emit (EmitContext ec)
		{
			// It's null for ResolveLValue used without assignment
			if (binder_expr == null)
				EmitCall (ec, setter, Arguments, false);
			else
				base.Emit (ec);
		}

		public override void EmitStatement (EmitContext ec)
		{
			// It's null for ResolveLValue used without assignment
			if (binder_expr == null)
				EmitCall (ec, setter, Arguments, true);
			else
				base.EmitStatement (ec);
		}

		#region IAssignMethod Members

		public void Emit (EmitContext ec, bool leave_copy)
		{
			throw new NotImplementedException ();
		}

		public void EmitAssign (EmitContext ec, Expression source, bool leave_copy, bool prepare_for_load)
		{
			EmitCall (ec, setter, setter_args, !leave_copy);
		}

		#endregion
	}

	class DynamicUnaryConversion : DynamicExpressionStatement, IDynamicBinder
	{
		string name;

		public DynamicUnaryConversion (string name, Arguments args, Location loc)
			: base (null, args, loc)
		{
			this.name = name;
			base.binder = this;
			if (name == "IsTrue" || name == "IsFalse")
				type = TypeManager.bool_type;
		}

		public Expression CreateCallSiteBinder (ResolveContext ec, Arguments args)
		{
			Arguments binder_args = new Arguments (4);

			MemberAccess sle = new MemberAccess (new MemberAccess (
				new QualifiedAliasMember (QualifiedAliasMember.GlobalAlias, "System", loc), "Linq", loc), "Expressions", loc);

			var flags = ec.HasSet (ResolveContext.Options.CheckedScope) ? CSharpBinderFlags.CheckedContext : 0;

			binder_args.Add (new Argument (new BinderFlags (flags, this)));
			binder_args.Add (new Argument (new MemberAccess (new MemberAccess (sle, "ExpressionType", loc), name, loc)));
			binder_args.Add (new Argument (new TypeOf (new TypeExpression (ec.CurrentType, loc), loc)));
			binder_args.Add (new Argument (new ImplicitlyTypedArrayCreation ("[]", args.CreateDynamicBinderArguments (ec), loc)));

			return new Invocation (GetBinder ("UnaryOperation", loc), binder_args);
		}
	}
}
