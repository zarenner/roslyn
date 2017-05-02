﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CodeStyle;
using Microsoft.CodeAnalysis.CSharp.CodeStyle;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Options;

namespace Microsoft.CodeAnalysis.CSharp.UseExpressionBody
{
    internal abstract class UseExpressionBodyHelper
    {
        public abstract Option<CodeStyleOption<ExpressionBodyPreference>> Option { get; }
        public abstract LocalizableString UseExpressionBodyTitle { get; }
        public abstract LocalizableString UseBlockBodyTitle { get; }
        public abstract string DiagnosticId { get; }
        public abstract ImmutableArray<SyntaxKind> SyntaxKinds { get; }

        public abstract BlockSyntax GetBody(SyntaxNode declaration);
        public abstract ArrowExpressionClauseSyntax GetExpressionBody(SyntaxNode declaration);

        public abstract bool CanOfferUseExpressionBody(OptionSet optionSet, SyntaxNode declaration, bool forAnalyzer);
        public abstract bool CanOfferUseBlockBody(OptionSet optionSet, SyntaxNode declaration, bool forAnalyzer);
        public abstract SyntaxNode Update(SyntaxNode declaration, OptionSet options);

        public static readonly ImmutableArray<UseExpressionBodyHelper> Helpers =
            ImmutableArray.Create<UseExpressionBodyHelper>(
                UseExpressionBodyForConstructorsHelper.Instance,
                UseExpressionBodyForConversionOperatorsHelper.Instance,
                UseExpressionBodyForIndexersHelper.Instance,
                UseExpressionBodyForMethodsHelper.Instance,
                UseExpressionBodyForOperatorsHelper.Instance,
                UseExpressionBodyForPropertiesHelper.Instance,
                UseExpressionBodyForAccessorsHelper.Instance);
    }

    /// <summary>
    /// Helper class that allows us to share lots of logic between the diagnostic analyzer and the
    /// code refactoring provider.  Those can't share a common base class due to their own inheritance
    /// requirements with <see cref="DiagnosticAnalyzer"/> and <see cref="CodeRefactoringProvider"/>.
    /// </summary>
    internal abstract class AbstractUseExpressionBodyHelper<TDeclaration> : UseExpressionBodyHelper
        where TDeclaration : SyntaxNode
    {
        public override Option<CodeStyleOption<ExpressionBodyPreference>> Option { get; }
        public override LocalizableString UseExpressionBodyTitle { get; }
        public override LocalizableString UseBlockBodyTitle { get; }
        public override string DiagnosticId { get; }
        public override ImmutableArray<SyntaxKind> SyntaxKinds { get; }

        protected AbstractUseExpressionBodyHelper(
            string diagnosticId,
            LocalizableString useExpressionBodyTitle,
            LocalizableString useBlockBodyTitle,
            Option<CodeStyleOption<ExpressionBodyPreference>> option,
            ImmutableArray<SyntaxKind> syntaxKinds)
        {
            DiagnosticId = diagnosticId;
            Option = option;
            UseExpressionBodyTitle = useExpressionBodyTitle;
            UseBlockBodyTitle = useBlockBodyTitle;
            SyntaxKinds = syntaxKinds;
        }

        protected static BlockSyntax GetBodyFromSingleGetAccessor(AccessorListSyntax accessorList)
        {
            if (accessorList != null &&
                accessorList.Accessors.Count == 1 &&
                accessorList.Accessors[0].AttributeLists.Count == 0 && 
                accessorList.Accessors[0].IsKind(SyntaxKind.GetAccessorDeclaration))
            {
                return accessorList.Accessors[0].Body;
            }

            return null;
        }

        public override BlockSyntax GetBody(SyntaxNode declaration)
            => GetBody((TDeclaration)declaration);

        public override ArrowExpressionClauseSyntax GetExpressionBody(SyntaxNode declaration)
            => GetExpressionBody((TDeclaration)declaration);

        public override bool CanOfferUseExpressionBody(OptionSet optionSet, SyntaxNode declaration, bool forAnalyzer)
            => CanOfferUseExpressionBody(optionSet, (TDeclaration)declaration, forAnalyzer);

        public override bool CanOfferUseBlockBody(OptionSet optionSet, SyntaxNode declaration, bool forAnalyzer)
            => CanOfferUseBlockBody(optionSet, (TDeclaration)declaration, forAnalyzer);

        public override SyntaxNode Update(SyntaxNode declaration, OptionSet options)
            => Update((TDeclaration)declaration, options);

        public virtual bool CanOfferUseExpressionBody(
            OptionSet optionSet, TDeclaration declaration, bool forAnalyzer)
        {
            var preference = optionSet.GetOption(this.Option).Value;
            var userPrefersExpressionBodies = preference != ExpressionBodyPreference.Never;

            // If the user likes expression bodies, then we offer expression bodies from the diagnostic analyzer.
            // If the user does not like expression bodies then we offer expression bodies from the refactoring provider.
            if (userPrefersExpressionBodies == forAnalyzer)
            {
                var expressionBody = this.GetExpressionBody(declaration);
                if (expressionBody == null)
                {
                    // They don't have an expression body.  See if we could convert the block they 
                    // have into one.

                    var options = declaration.SyntaxTree.Options;
                    var body = this.GetBody(declaration);

                    var conversionPreference = forAnalyzer ? preference : ExpressionBodyPreference.WhenPossible;

                    if (body.TryConvertToExpressionBody(options, conversionPreference,
                            out var expressionWhenOnSingleLine, out var semicolonWhenOnSingleLine))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public virtual bool CanOfferUseBlockBody(
            OptionSet optionSet, TDeclaration declaration, bool forAnalyzer)
        {
            var preference = optionSet.GetOption(this.Option).Value;
            var userPrefersBlockBodies = preference == ExpressionBodyPreference.Never;

            // If the user likes block bodies, then we offer block bodies from the diagnostic analyzer.
            // If the user does not like block bodies then we offer block bodies from the refactoring provider.
            if (userPrefersBlockBodies == forAnalyzer)
            {
                // If we have an expression body, we can always convert it to a block body.
                return this.GetExpressionBody(declaration) != null;
            }

            return false;
        }

        public TDeclaration Update(TDeclaration declaration, OptionSet options)
        {
            var preferExpressionBody = GetBody(declaration) != null;
            if (preferExpressionBody)
            {
                GetBody(declaration).TryConvertToExpressionBody(declaration.SyntaxTree.Options,
                    ExpressionBodyPreference.WhenPossible, out var expressionBody, out var semicolonToken);

                var trailingTrivia = semicolonToken.TrailingTrivia
                                                   .Where(t => t.Kind() != SyntaxKind.EndOfLineTrivia)
                                                   .Concat(declaration.GetTrailingTrivia());
                semicolonToken = semicolonToken.WithTrailingTrivia(trailingTrivia);

                return WithSemicolonToken(
                           WithExpressionBody(
                               WithBody(declaration, null),
                               expressionBody),
                           semicolonToken);
            }
            else
            {
                return WithSemicolonToken(
                           WithExpressionBody(
                               WithGenerateBody(declaration, options),
                               null),
                           default(SyntaxToken));
            }
        }

        protected abstract BlockSyntax GetBody(TDeclaration declaration);

        protected abstract ArrowExpressionClauseSyntax GetExpressionBody(TDeclaration declaration);

        protected abstract bool CreateReturnStatementForExpression(TDeclaration declaration);

        protected abstract SyntaxToken GetSemicolonToken(TDeclaration declaration);

        protected abstract TDeclaration WithSemicolonToken(TDeclaration declaration, SyntaxToken token);
        protected abstract TDeclaration WithExpressionBody(TDeclaration declaration, ArrowExpressionClauseSyntax expressionBody);
        protected abstract TDeclaration WithBody(TDeclaration declaration, BlockSyntax body);

        protected virtual TDeclaration WithGenerateBody(
            TDeclaration declaration, OptionSet options)
        {
            var expressionBody = GetExpressionBody(declaration);
            var semicolonToken = GetSemicolonToken(declaration);
            var block = expressionBody.ConvertToBlock(
                GetSemicolonToken(declaration),
                CreateReturnStatementForExpression(declaration));

            return WithBody(declaration, block);
        }

        protected TDeclaration WithAccessorList(
            TDeclaration declaration, OptionSet options)
        {
            var expressionBody = GetExpressionBody(declaration);
            var semicolonToken = GetSemicolonToken(declaration);

            var expressionBodyPreference = options.GetOption(CSharpCodeStyleOptions.PreferExpressionBodiedAccessors).Value;

            AccessorDeclarationSyntax accessor;
            if (expressionBodyPreference != ExpressionBodyPreference.Never)
            {
                accessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                                        .WithExpressionBody(expressionBody)
                                        .WithSemicolonToken(semicolonToken);
            }
            else
            {
                var block = expressionBody.ConvertToBlock(
                    GetSemicolonToken(declaration),
                    CreateReturnStatementForExpression(declaration));
                accessor = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration, block);
            }

            return WithAccessorList(declaration, SyntaxFactory.AccessorList(
                SyntaxFactory.SingletonList(accessor)));
        }

        protected virtual TDeclaration WithAccessorList(TDeclaration declaration, AccessorListSyntax accessorListSyntax)
        {
            throw new NotImplementedException();
        }
    }
}