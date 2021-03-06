// Copyright 2004-2011 Castle Project - http://www.castleproject.org/
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace Castle.DynamicProxy.Contributors
{
	using System;
	using System.Reflection;
#if FEATURE_SERIALIZATION
	using System.Runtime.Serialization;
#endif

	using Castle.DynamicProxy.Generators;
	using Castle.DynamicProxy.Generators.Emitters;
	using Castle.DynamicProxy.Generators.Emitters.CodeBuilders;
	using Castle.DynamicProxy.Generators.Emitters.SimpleAST;
	using Castle.DynamicProxy.Internal;
#if FEATURE_SERIALIZATION
	using Castle.DynamicProxy.Serialization;
#endif
	using Castle.DynamicProxy.Tokens;

	internal abstract class ProxyInstanceContributor : ITypeContributor
	{
		protected readonly Type targetType;
		private readonly string proxyTypeId;
		private readonly Type[] interfaces;

		protected ProxyInstanceContributor(Type targetType, Type[] interfaces, string proxyTypeId)
		{
			this.targetType = targetType;
			this.proxyTypeId = proxyTypeId;
			this.interfaces = interfaces ?? Type.EmptyTypes;
		}

		protected abstract Reference GetTargetReference(ClassEmitter emitter);

		private Expression GetTargetReferenceExpression(ClassEmitter emitter)
		{
			return GetTargetReference(emitter).ToExpression();
		}

		public virtual void Generate(ClassEmitter @class)
		{
			var interceptors = @class.GetField("__interceptors");
#if FEATURE_SERIALIZATION
			ImplementGetObjectData(@class);
#endif
			ImplementProxyTargetAccessor(@class, interceptors);
			foreach (var attribute in targetType.GetNonInheritableAttributes())
			{
				@class.DefineCustomAttribute(attribute.Builder);
			}
		}

		protected void ImplementProxyTargetAccessor(ClassEmitter emitter, FieldReference interceptorsField)
		{
			var dynProxyGetTarget = emitter.CreateMethod("DynProxyGetTarget", typeof(object));

			dynProxyGetTarget.CodeBuilder.AddStatement(
				new ReturnStatement(new ConvertExpression(typeof(object), targetType, GetTargetReferenceExpression(emitter))));

			var dynProxySetTarget = emitter.CreateMethod("DynProxySetTarget", typeof(void), typeof(object));

			// we can only change the target of the interface proxy
			var targetField = GetTargetReference(emitter) as FieldReference;
			if (targetField != null)
			{
				dynProxySetTarget.CodeBuilder.AddStatement(
					new AssignStatement(targetField,
						new ConvertExpression(targetField.Fieldbuilder.FieldType, dynProxySetTarget.Arguments[0].ToExpression())));
			}
			else
			{
				dynProxySetTarget.CodeBuilder.AddStatement(
					new ThrowStatement(typeof(InvalidOperationException), "Cannot change the target of the class proxy."));
			}

			dynProxySetTarget.CodeBuilder.AddStatement(new ReturnStatement());

			var getInterceptors = emitter.CreateMethod("GetInterceptors", typeof(IInterceptor[]));

			getInterceptors.CodeBuilder.AddStatement(
				new ReturnStatement(interceptorsField));
		}

#if FEATURE_SERIALIZATION
		protected void ImplementGetObjectData(ClassEmitter emitter)
		{
			var getObjectData = emitter.CreateMethod("GetObjectData", typeof(void),
			                                         new[] { typeof(SerializationInfo), typeof(StreamingContext) });
			var info = getObjectData.Arguments[0];

			var typeLocal = getObjectData.CodeBuilder.DeclareLocal(typeof(Type));

			getObjectData.CodeBuilder.AddStatement(
				new AssignStatement(
					typeLocal,
					new MethodInvocationExpression(
						null,
						TypeMethods.StaticGetType,
						new ConstReference(typeof(ProxyObjectReference).AssemblyQualifiedName).ToExpression(),
						new ConstReference(1).ToExpression(),
						new ConstReference(0).ToExpression())));

			getObjectData.CodeBuilder.AddStatement(
				new ExpressionStatement(
					new MethodInvocationExpression(
						info,
						SerializationInfoMethods.SetType,
						typeLocal.ToExpression())));

			foreach (var field in emitter.GetAllFields())
			{
				if (field.Reference.IsStatic)
				{
					continue;
				}
				if (field.Reference.IsNotSerialized)
				{
					continue;
				}
				AddAddValueInvocation(info, getObjectData, field);
			}

			var interfacesLocal = getObjectData.CodeBuilder.DeclareLocal(typeof(string[]));

			getObjectData.CodeBuilder.AddStatement(
				new AssignStatement(
					interfacesLocal,
					new NewArrayExpression(interfaces.Length, typeof(string))));

			for (var i = 0; i < interfaces.Length; i++)
			{
				getObjectData.CodeBuilder.AddStatement(
					new AssignArrayStatement(
						interfacesLocal,
						i,
						new ConstReference(interfaces[i].AssemblyQualifiedName).ToExpression()));
			}

			getObjectData.CodeBuilder.AddStatement(
				new ExpressionStatement(
					new MethodInvocationExpression(
						info,
						SerializationInfoMethods.AddValue_Object,
						new ConstReference("__interfaces").ToExpression(),
						interfacesLocal.ToExpression())));

			getObjectData.CodeBuilder.AddStatement(
				new ExpressionStatement(
					new MethodInvocationExpression(
						info,
						SerializationInfoMethods.AddValue_Object,
						new ConstReference("__baseType").ToExpression(),
						new ConstReference(emitter.BaseType.AssemblyQualifiedName).ToExpression())));

			getObjectData.CodeBuilder.AddStatement(
				new ExpressionStatement(
					new MethodInvocationExpression(
						info,
						SerializationInfoMethods.AddValue_Object,
						new ConstReference("__proxyGenerationOptions").ToExpression(),
						emitter.GetField("proxyGenerationOptions").ToExpression())));

			getObjectData.CodeBuilder.AddStatement(
				new ExpressionStatement(
					new MethodInvocationExpression(info,
					                               SerializationInfoMethods.AddValue_Object,
					                               new ConstReference("__proxyTypeId").ToExpression(),
					                               new ConstReference(proxyTypeId).ToExpression())));

			CustomizeGetObjectData(getObjectData.CodeBuilder, info, getObjectData.Arguments[1], emitter);

			getObjectData.CodeBuilder.AddStatement(new ReturnStatement());
		}

		protected virtual void AddAddValueInvocation(ArgumentReference serializationInfo, MethodEmitter getObjectData,
		                                             FieldReference field)
		{
			getObjectData.CodeBuilder.AddStatement(
				new ExpressionStatement(
					new MethodInvocationExpression(
						serializationInfo,
						SerializationInfoMethods.AddValue_Object,
						new ConstReference(field.Reference.Name).ToExpression(),
						field.ToExpression())));
			return;
		}

		protected abstract void CustomizeGetObjectData(AbstractCodeBuilder builder, ArgumentReference serializationInfo,
		                                               ArgumentReference streamingContext, ClassEmitter emitter);
#endif

		public void CollectElementsToProxy(IProxyGenerationHook hook, MetaType model)
		{
		}
	}
}