﻿using System.Collections.Generic;
using System.Linq;

namespace Microsoft.Boogie
{
  public enum LinearKind
  {
    ORDINARY,
    LINEAR,
    LINEAR_IN,
    LINEAR_OUT
  }

  public class LinearQualifier
  {
    public string domainName;
    public LinearKind kind;

    public LinearQualifier(string domainName, LinearKind kind)
    {
      this.domainName = domainName;
      this.kind = kind;
    }
  }

  public class LinearDomain
  {
    public string domainName;
    public Type permissionType;
    public Dictionary<Type, Function> collectors;
    public MapType mapTypeBool;
    public MapType mapTypeInt;
    public Function mapConstBool;
    public Function mapConstInt;
    public Function mapOrBool;
    public Function mapImpBool;
    public Function mapEqInt;
    public Function mapAddInt;
    public Function mapIteInt;
    public Function mapLeInt;

    public LinearDomain(Program program, string domainName, Type permissionType, Dictionary<Type, Function> collectors)
    {
      this.domainName = domainName;
      this.permissionType = permissionType;
      this.collectors = collectors;

      this.mapTypeBool = new MapType(Token.NoToken, new List<TypeVariable>(), new List<Type> {this.permissionType},
        Type.Bool);
      this.mapTypeInt = new MapType(Token.NoToken, new List<TypeVariable>(), new List<Type> {this.permissionType},
        Type.Int);

      this.mapConstBool = program.monomorphizer.Monomorphize("MapConst",
        new Dictionary<string, Type>() { {"T", permissionType}, {"U", Type.Bool} });
      this.mapConstInt = program.monomorphizer.Monomorphize("MapConst",
        new Dictionary<string, Type>() { {"T", permissionType}, {"U", Type.Int} });
      this.mapOrBool = program.monomorphizer.Monomorphize("MapOr",
        new Dictionary<string, Type>() { {"T", permissionType} });
      this.mapImpBool = program.monomorphizer.Monomorphize("MapImp",
        new Dictionary<string, Type>() { {"T", permissionType} });
      this.mapEqInt = program.monomorphizer.Monomorphize("MapEq",
        new Dictionary<string, Type>() { {"T", permissionType}, {"U", Type.Int} });
      this.mapAddInt = program.monomorphizer.Monomorphize("MapAdd",
        new Dictionary<string, Type>() { {"T", permissionType} });
      this.mapIteInt = program.monomorphizer.Monomorphize("MapIte",
        new Dictionary<string, Type>() { {"T", permissionType}, {"U", Type.Int} });
      this.mapLeInt = program.monomorphizer.Monomorphize("MapLe",
        new Dictionary<string, Type>() { {"T", permissionType} });
    }
  }

  /// <summary>
  /// Type checker for linear type annotations.
  /// 
  /// The functionality is basically grouped into four parts (see #region's).
  /// 1) TypeCheck parses linear type attributes, sets up the data structures,
  ///    and performs a dataflow check on procedure implementations.
  /// 2) Useful public methods to generate expressions for permissions, their disjointness,
  ///    and their union.
  /// 3) Generation of linearity-invariant checker procedures for atomic actions.
  /// 4) Erasure procedure to remove all linearity attributes
  ///    (invoked after all other CIVL transformations).
  /// </summary>
  public class LinearTypeChecker : ReadOnlyVisitor
  {
    public Program program;
    public CheckingContext checkingContext;
    public Dictionary<string, LinearDomain> linearDomains;

    private CivlTypeChecker civlTypeChecker;

    private Dictionary<Absy, HashSet<Variable>> availableLinearVars;
    private Dictionary<Variable, LinearQualifier> inParamToLinearQualifier;
    private Dictionary<Variable, string> outParamToDomainName;
    private Dictionary<Variable, string> globalVarToDomainName;

    // Only used in visitor implementation
    private Dictionary<string, Dictionary<Type, Function>> domainNameToCollectors;
    private Dictionary<Variable, string> varToDomainName;

    public LinearTypeChecker(CivlTypeChecker civlTypeChecker)
    {
      this.civlTypeChecker = civlTypeChecker;
      this.program = civlTypeChecker.program;
      this.checkingContext = civlTypeChecker.checkingContext;
      this.domainNameToCollectors = new Dictionary<string, Dictionary<Type, Function>>();
      this.availableLinearVars = new Dictionary<Absy, HashSet<Variable>>();
      this.inParamToLinearQualifier = new Dictionary<Variable, LinearQualifier>();
      this.outParamToDomainName = new Dictionary<Variable, string>();
      this.globalVarToDomainName = new Dictionary<Variable, string>();
      this.linearDomains = new Dictionary<string, LinearDomain>();
      this.varToDomainName = new Dictionary<Variable, string>();
    }

    private void Error(Absy node, string message)
    {
      checkingContext.Error(node, message);
    }

    public string FindDomainName(Variable v)
    {
      if (globalVarToDomainName.ContainsKey(v))
        return globalVarToDomainName[v];
      if (inParamToLinearQualifier.ContainsKey(v))
        return inParamToLinearQualifier[v].domainName;
      if (outParamToDomainName.ContainsKey(v))
        return outParamToDomainName[v];
      string domainName = QKeyValue.FindStringAttribute(v.Attributes, CivlAttributes.LINEAR);
      if (domainName != null)
        return domainName;
      domainName = QKeyValue.FindStringAttribute(v.Attributes, CivlAttributes.LINEAR_IN);
      if (domainName != null)
        return domainName;
      return QKeyValue.FindStringAttribute(v.Attributes, CivlAttributes.LINEAR_OUT);
    }

    public LinearKind FindLinearKind(Variable v)
    {
      if (globalVarToDomainName.ContainsKey(v))
        return LinearKind.LINEAR;
      if (inParamToLinearQualifier.ContainsKey(v))
        return inParamToLinearQualifier[v].kind;
      if (outParamToDomainName.ContainsKey(v))
        return LinearKind.LINEAR;

      if (QKeyValue.FindStringAttribute(v.Attributes, CivlAttributes.LINEAR) != null)
      {
        return LinearKind.LINEAR;
      }
      else if (QKeyValue.FindStringAttribute(v.Attributes, CivlAttributes.LINEAR_IN) != null)
      {
        return LinearKind.LINEAR_IN;
      }
      else if (QKeyValue.FindStringAttribute(v.Attributes, CivlAttributes.LINEAR_OUT) != null)
      {
        return LinearKind.LINEAR_OUT;
      }
      else
      {
        return LinearKind.ORDINARY;
      }
    }

    public Formal LinearDomainInFormal(string domainName)
    {
      return civlTypeChecker.Formal("linear_" + domainName + "_in", linearDomains[domainName].mapTypeBool, true);
    }

    public LocalVariable LinearDomainAvailableLocal(string domainName)
    {
      return civlTypeChecker.LocalVariable("linear_" + domainName + "_available", linearDomains[domainName].mapTypeBool);
    }

    private static List<string> FindDomainNames(QKeyValue kv)
    {
      List<string> domains = new List<string>();
      for (; kv != null; kv = kv.Next)
      {
        if (kv.Key != CivlAttributes.LINEAR) continue;
        foreach (var o in kv.Params)
        {
          if (o is string s)
          {
            domains.Add(s);
          }
        }
      }
      return domains;
    }
    
    public void TypeCheck()
    {
      this.VisitProgram(program);
      
      var permissionTypes = GetPermissionTypes();
      ProcessCollectors(permissionTypes);
      
      if (checkingContext.ErrorCount > 0)
      {
        return;
      }
      foreach ((var domainName, var collectors) in domainNameToCollectors)
      {
        if (collectors.Count != 0)
        {
          this.linearDomains[domainName] =
            new LinearDomain(program, domainName, permissionTypes[domainName], collectors);
        }
      }
      foreach (Absy absy in this.availableLinearVars.Keys)
      {
        availableLinearVars[absy].RemoveWhere(v => v is GlobalVariable);
      }
    }

    private Dictionary<string, Type> GetPermissionTypes()
    {
      var permissionTypes = new Dictionary<string, Type>();
      foreach (var decl in program.TopLevelDeclarations.Where(decl => decl is TypeCtorDecl || decl is TypeSynonymDecl))
      {
        foreach (var domainName in FindDomainNames(decl.Attributes))
        {
          if (permissionTypes.ContainsKey(domainName))
          {
            Error(decl, $"Duplicate permission type for domain {domainName}");
          }
          else if (decl is TypeCtorDecl typeCtorDecl)
          {
            if (typeCtorDecl.Arity > 0)
            {
              Error(decl, "Permission type must be fully instantiated");
            }
            else
            {
              permissionTypes[domainName] = new CtorType(Token.NoToken, typeCtorDecl, new List<Type>());
            }
          }
          else
          {
            permissionTypes[domainName] =
              new TypeSynonymAnnotation(Token.NoToken, (TypeSynonymDecl) decl, new List<Type>());
          }
        }
      }
      return permissionTypes;
    }

    private void ProcessCollectors(Dictionary<string, Type> permissionTypes)
    {
      foreach (var variable in varToDomainName.Keys)
      {
        string domainName = FindDomainName(variable);
        if (!permissionTypes.ContainsKey(domainName))
        {
          Error(variable, $"Permission type not declared for domain {domainName}");
          continue;
        }
        var permissionType = permissionTypes[domainName];
        if (!domainNameToCollectors.ContainsKey(domainName))
        {
          domainNameToCollectors[domainName] = new Dictionary<Type, Function>();
        }
        var variableType = variable.TypedIdent.Type;
        if (!domainNameToCollectors[domainName].ContainsKey(variableType))
        {
          if (variableType.Equals(permissionType))
          {
            // add unit collector
            domainNameToCollectors[domainName][variableType] =
              program.monomorphizer.Monomorphize("MapUnit", new Dictionary<string, Type>() { {"T", variableType} });
          }
          else if (variableType.Equals(new MapType(Token.NoToken, new List<TypeVariable>(), new List<Type>{permissionType}, Type.Bool)))
          {
            // add identity collector
            domainNameToCollectors[domainName][variableType] =
              program.monomorphizer.Monomorphize("Id", new Dictionary<string, Type>() { {"T", variableType} });
          }
          else
          {
            Error(variable, "Missing collector for linear variable " + variable.Name);
          }
        }
      }
    }

    #region Visitor Implementation

    public override Program VisitProgram(Program node)
    {
      foreach (GlobalVariable g in program.GlobalVariables)
      {
        string domainName = FindDomainName(g);
        if (domainName != null)
        {
          globalVarToDomainName[g] = domainName;
        }
      }

      return base.VisitProgram(node);
    }

    public override Function VisitFunction(Function node)
    {
      string domainName = QKeyValue.FindStringAttribute(node.Attributes, CivlAttributes.LINEAR);
      if (domainName != null)
      {
        if (!domainNameToCollectors.ContainsKey(domainName))
        {
          domainNameToCollectors[domainName] = new Dictionary<Type, Function>();
        }

        if (node.InParams.Count == 1 && node.OutParams.Count == 1)
        {
          Type inType = node.InParams[0].TypedIdent.Type;
          MapType outType = node.OutParams[0].TypedIdent.Type as MapType;
          if (domainNameToCollectors[domainName].ContainsKey(inType))
          {
            Error(node, "A collector for domain for input type has already been defined");
          }
          else if (outType == null || outType.Arguments.Count != 1 || !outType.Result.Equals(Type.Bool))
          {
            Error(node, "Output of a linear domain collector should be of set type");
          }
          else
          {
            domainNameToCollectors[domainName][inType] = node;
          }
        }
        else
        {
          Error(node, "Linear domain collector should have one input and one output parameter");
        }
      }

      return base.VisitFunction(node);
    }

    public override Implementation VisitImplementation(Implementation node)
    {
      if (civlTypeChecker.procToAtomicAction.ContainsKey(node.Proc) ||
          civlTypeChecker.procToIntroductionAction.ContainsKey(node.Proc) ||
          civlTypeChecker.procToLemmaProc.ContainsKey(node.Proc))
        return node;

      node.PruneUnreachableBlocks();
      node.ComputePredecessorsForBlocks();
      GraphUtil.Graph<Block> graph = Program.GraphFromImpl(node);
      graph.ComputeLoops();

      HashSet<Variable> start = new HashSet<Variable>(globalVarToDomainName.Keys);
      for (int i = 0; i < node.InParams.Count; i++)
      {
        Variable v = node.Proc.InParams[i];
        string domainName = FindDomainName(v);
        if (domainName != null)
        {
          var kind = FindLinearKind(v);
          inParamToLinearQualifier[node.InParams[i]] = new LinearQualifier(domainName, kind);
          if (kind == LinearKind.LINEAR || kind == LinearKind.LINEAR_IN)
          {
            start.Add(node.InParams[i]);
          }
        }
      }

      for (int i = 0; i < node.OutParams.Count; i++)
      {
        string domainName = FindDomainName(node.Proc.OutParams[i]);
        if (domainName != null)
        {
          outParamToDomainName[node.OutParams[i]] = domainName;
        }
      }

      var oldErrorCount = checkingContext.ErrorCount;
      var impl = base.VisitImplementation(node);
      if (oldErrorCount < checkingContext.ErrorCount)
        return impl;

      Stack<Block> dfsStack = new Stack<Block>();
      HashSet<Block> dfsStackAsSet = new HashSet<Block>();
      availableLinearVars[node.Blocks[0]] = start;
      dfsStack.Push(node.Blocks[0]);
      dfsStackAsSet.Add(node.Blocks[0]);
      while (dfsStack.Count > 0)
      {
        Block b = dfsStack.Pop();
        dfsStackAsSet.Remove(b);
        HashSet<Variable> end = PropagateAvailableLinearVarsAcrossBlock(b);
        if (b.TransferCmd is ReturnCmd)
        {
          foreach (GlobalVariable g in globalVarToDomainName.Keys.Except(end))
          {
            Error(b.TransferCmd, $"Global variable {g.Name} must be available at a return");
          }

          foreach (Variable v in node.InParams)
          {
            if (FindDomainName(v) == null || FindLinearKind(v) == LinearKind.LINEAR_IN || end.Contains(v)) continue;
            Error(b.TransferCmd, $"Input variable {v.Name} must be available at a return");
          }

          foreach (Variable v in node.OutParams)
          {
            if (FindDomainName(v) == null || end.Contains(v)) continue;
            Error(b.TransferCmd, $"Output variable {v.Name} must be available at a return");
          }

          continue;
        }

        GotoCmd gotoCmd = b.TransferCmd as GotoCmd;
        foreach (Block target in gotoCmd.labelTargets)
        {
          if (!availableLinearVars.ContainsKey(target))
          {
            availableLinearVars[target] = new HashSet<Variable>(end);
            dfsStack.Push(target);
            dfsStackAsSet.Add(target);
          }
          else
          {
            var savedAvailableVars = new HashSet<Variable>(availableLinearVars[target]);
            availableLinearVars[target].IntersectWith(end);
            if (savedAvailableVars.IsProperSupersetOf(availableLinearVars[target]) && !dfsStackAsSet.Contains(target))
            {
              dfsStack.Push(target);
              dfsStackAsSet.Add(target);
            }
          }
        }
      }

      if (graph.Reducible)
      {
        foreach (Block header in graph.Headers)
        {
          foreach (GlobalVariable g in globalVarToDomainName.Keys.Except(availableLinearVars[header]))
          {
            Error(header, $"Global variable {g.Name} must be available at a loop head");
          }
        }
      }

      return impl;
    }

    private void AddAvailableVars(CallCmd callCmd, HashSet<Variable> start)
    {
      foreach (IdentifierExpr ie in callCmd.Outs)
      {
        if (FindDomainName(ie.Decl) == null) continue;
        start.Add(ie.Decl);
      }

      for (int i = 0; i < callCmd.Proc.InParams.Count; i++)
      {
        if (callCmd.Ins[i] is IdentifierExpr ie)
        {
          Variable v = callCmd.Proc.InParams[i];
          if (FindDomainName(v) == null) continue;
          if (FindLinearKind(v) == LinearKind.LINEAR_OUT)
          {
            start.Add(ie.Decl);
          }
        }
      }
    }

    private void AddAvailableVars(ParCallCmd parCallCmd, HashSet<Variable> start)
    {
      foreach (CallCmd callCmd in parCallCmd.CallCmds)
      {
        AddAvailableVars(callCmd, start);
      }
    }

    private HashSet<Variable> PropagateAvailableLinearVarsAcrossBlock(Block b)
    {
      HashSet<Variable> start = new HashSet<Variable>(availableLinearVars[b]);
      foreach (Cmd cmd in b.Cmds)
      {
        if (cmd is AssignCmd assignCmd)
        {
          for (int i = 0; i < assignCmd.Lhss.Count; i++)
          {
            if (FindDomainName(assignCmd.Lhss[i].DeepAssignedVariable) == null) continue;
            IdentifierExpr ie = assignCmd.Rhss[i] as IdentifierExpr;
            if (!start.Contains(ie.Decl))
            {
              Error(ie, "unavailable source for a linear read");
            }
            else
            {
              start.Remove(ie.Decl);
            }
          }

          foreach (AssignLhs assignLhs in assignCmd.Lhss)
          {
            if (FindDomainName(assignLhs.DeepAssignedVariable) == null) continue;
            start.Add(assignLhs.DeepAssignedVariable);
          }
        }
        else if (cmd is CallCmd callCmd)
        {
          foreach (GlobalVariable g in globalVarToDomainName.Keys.Except(start))
          {
            Error(cmd, $"Global variable {g.Name} must be available at a call");
          }

          for (int i = 0; i < callCmd.Proc.InParams.Count; i++)
          {
            Variable param = callCmd.Proc.InParams[i];
            if (FindDomainName(param) == null) continue;
            IdentifierExpr ie = callCmd.Ins[i] as IdentifierExpr;
            LinearKind paramKind = FindLinearKind(param);
            if (start.Contains(ie.Decl))
            {
              if (callCmd.IsAsync || paramKind == LinearKind.LINEAR_IN)
              {
                start.Remove(ie.Decl);
              }
            }
            else
            {
              if (paramKind == LinearKind.LINEAR_OUT)
              {
                start.Add(ie.Decl);
              }
              else
              {
                Error(ie, "unavailable source for a linear read");
              }
            }
          }

          AddAvailableVars(callCmd, start);
          availableLinearVars[callCmd] = new HashSet<Variable>(start);
        }
        else if (cmd is ParCallCmd parCallCmd)
        {
          foreach (GlobalVariable g in globalVarToDomainName.Keys.Except(start))
          {
            Error(cmd, $"Global variable {g.Name} must be available at a call");
          }

          foreach (CallCmd parCallCallCmd in parCallCmd.CallCmds)
          {
            for (int i = 0; i < parCallCallCmd.Proc.InParams.Count; i++)
            {
              Variable param = parCallCallCmd.Proc.InParams[i];
              if (FindDomainName(param) == null) continue;
              IdentifierExpr ie = parCallCallCmd.Ins[i] as IdentifierExpr;
              LinearKind paramKind = FindLinearKind(param);
              if (start.Contains(ie.Decl))
              {
                if (paramKind == LinearKind.LINEAR_IN)
                {
                  start.Remove(ie.Decl);
                }
              }
              else
              {
                if (paramKind == LinearKind.LINEAR_OUT)
                {
                  start.Add(ie.Decl);
                }
                else
                {
                  Error(ie, "unavailable source for a linear read");
                }
              }
            }
          }

          AddAvailableVars(parCallCmd, start);
          availableLinearVars[parCallCmd] = new HashSet<Variable>(start);
        }
        else if (cmd is HavocCmd havocCmd)
        {
          foreach (IdentifierExpr ie in havocCmd.Vars)
          {
            if (FindDomainName(ie.Decl) == null) continue;
            start.Remove(ie.Decl);
          }
        }
        else if (cmd is YieldCmd)
        {
          foreach (GlobalVariable g in globalVarToDomainName.Keys.Except(start))
          {
            Error(cmd, $"Global variable {g.Name} must be available at a yield");
          }

          availableLinearVars[cmd] = new HashSet<Variable>(start);
        }
      }

      return start;
    }

    public override Variable VisitVariable(Variable node)
    {
      string domainName = FindDomainName(node);
      if (domainName != null)
      {
        varToDomainName[node] = domainName;
        LinearKind kind = FindLinearKind(node);
        if (kind != LinearKind.LINEAR)
        {
          if (node is GlobalVariable || node is LocalVariable || (node is Formal formal && !formal.InComing))
          {
            Error(node, "Variable must be declared linear (as opposed to linear_in or linear_out)");
          }
        }
      }

      return base.VisitVariable(node);
    }

    public override Cmd VisitAssignCmd(AssignCmd node)
    {
      HashSet<Variable> rhsVars = new HashSet<Variable>();
      for (int i = 0; i < node.Lhss.Count; i++)
      {
        AssignLhs lhs = node.Lhss[i];
        Variable lhsVar = lhs.DeepAssignedVariable;
        string domainName = FindDomainName(lhsVar);
        if (domainName == null) continue;
        if (!(lhs is SimpleAssignLhs))
        {
          Error(node, $"Only simple assignment allowed on linear variable {lhsVar.Name}");
          continue;
        }

        IdentifierExpr rhs = node.Rhss[i] as IdentifierExpr;
        if (rhs == null)
        {
          Error(node, $"Only variable can be assigned to linear variable {lhsVar.Name}");
          continue;
        }

        string rhsDomainName = FindDomainName(rhs.Decl);
        if (rhsDomainName == null)
        {
          Error(node, $"Only linear variable can be assigned to linear variable {lhsVar.Name}");
          continue;
        }

        if (domainName != rhsDomainName)
        {
          Error(node,
            $"Linear variable of domain {rhsDomainName} cannot be assigned to linear variable of domain {domainName}");
          continue;
        }

        if (rhsVars.Contains(rhs.Decl))
        {
          Error(node, $"Linear variable {rhs.Decl.Name} can occur only once in the right-hand-side of an assignment");
          continue;
        }

        rhsVars.Add(rhs.Decl);
      }

      return base.VisitAssignCmd(node);
    }

    public override Cmd VisitCallCmd(CallCmd node)
    {
      HashSet<Variable> inVars = new HashSet<Variable>();
      for (int i = 0; i < node.Proc.InParams.Count; i++)
      {
        Variable formal = node.Proc.InParams[i];
        string domainName = FindDomainName(formal);
        if (domainName == null) continue;
        IdentifierExpr actual = node.Ins[i] as IdentifierExpr;
        if (actual == null)
        {
          Error(node.Ins[i], $"Only variable can be passed to linear parameter {formal.Name}");
          continue;
        }

        string actualDomainName = FindDomainName(actual.Decl);
        if (actualDomainName == null)
        {
          Error(actual, $"Only a linear argument can be passed to linear parameter {formal.Name}");
          continue;
        }

        if (domainName != actualDomainName)
        {
          Error(actual, "The domains of formal and actual parameters must be the same");
          continue;
        }

        if (actual.Decl is GlobalVariable)
        {
          Error(actual, "Only local linear variable can be an actual input parameter of a procedure call");
          continue;
        }

        if (inVars.Contains(actual.Decl))
        {
          Error(node, $"Linear variable {actual.Decl.Name} can occur only once as an input parameter");
          continue;
        }

        inVars.Add(actual.Decl);
      }

      for (int i = 0; i < node.Proc.OutParams.Count; i++)
      {
        IdentifierExpr actual = node.Outs[i];
        string actualDomainName = FindDomainName(actual.Decl);
        if (actualDomainName == null) continue;
        Variable formal = node.Proc.OutParams[i];
        string domainName = FindDomainName(formal);
        if (domainName == null)
        {
          Error(node, "Only a linear variable can be passed to a linear parameter");
          continue;
        }

        if (domainName != actualDomainName)
        {
          Error(node, "The domains of formal and actual parameters must be the same");
          continue;
        }

        if (actual.Decl is GlobalVariable)
        {
          Error(node, "Only local linear variable can be actual output parameter of a procedure call");
          continue;
        }
      }

      return base.VisitCallCmd(node);
    }

    public override Cmd VisitParCallCmd(ParCallCmd node)
    {
      HashSet<Variable> parallelCallInvars = new HashSet<Variable>();
      foreach (CallCmd callCmd in node.CallCmds)
      {
        if (civlTypeChecker.procToYieldInvariant.ContainsKey(callCmd.Proc)) continue;
        for (int i = 0; i < callCmd.Proc.InParams.Count; i++)
        {
          Variable formal = callCmd.Proc.InParams[i];
          string domainName = FindDomainName(formal);
          if (domainName == null) continue;
          IdentifierExpr actual = callCmd.Ins[i] as IdentifierExpr;
          if (parallelCallInvars.Contains(actual.Decl))
          {
            Error(node,
              $"Linear variable {actual.Decl.Name} can occur only once as an input parameter of a parallel call");
          }
          else
          {
            parallelCallInvars.Add(actual.Decl);
          }
        }
      }

      foreach (CallCmd callCmd in node.CallCmds)
      {
        if (!civlTypeChecker.procToYieldInvariant.ContainsKey(callCmd.Proc)) continue;
        for (int i = 0; i < callCmd.Proc.InParams.Count; i++)
        {
          Variable formal = callCmd.Proc.InParams[i];
          string domainName = FindDomainName(formal);
          if (domainName == null) continue;
          IdentifierExpr actual = callCmd.Ins[i] as IdentifierExpr;
          if (parallelCallInvars.Contains(actual.Decl))
          {
            Error(node,
              $"Linear variable {actual.Decl.Name} cannot be an input parameter to both a yield invariant and a procedure in a parallel call");
          }
        }
      }

      return base.VisitParCallCmd(node);
    }

    public override Requires VisitRequires(Requires requires)
    {
      return requires;
    }

    public override Ensures VisitEnsures(Ensures ensures)
    {
      return ensures;
    }

    #endregion

    #region Useful public methods
    public ISet<Variable> AvailableLinearVars(Absy absy)
    {
      if (availableLinearVars.ContainsKey(absy))
      {
        return availableLinearVars[absy];
      }
      else
      {
        return new HashSet<Variable>();
      }
    }

    public IEnumerable<Expr> DisjointnessExprForEachDomain(IEnumerable<Variable> scope)
    {
      Dictionary<string, HashSet<Variable>> domainNameToScope = new Dictionary<string, HashSet<Variable>>();
      foreach (var domainName in linearDomains.Keys)
      {
        domainNameToScope[domainName] = new HashSet<Variable>();
      }

      foreach (Variable v in scope)
      {
        var domainName = FindDomainName(v);
        if (domainName == null) continue;
        domainNameToScope[domainName].Add(v);
      }

      foreach (string domainName in domainNameToScope.Keys)
      {
        yield return DisjointnessExprForPermissions(
          domainName,
          PermissionExprForEachVariable(domainName, domainNameToScope[domainName]));
      }
    }

    public IEnumerable<Expr> PermissionExprForEachVariable(string domainName, IEnumerable<Variable> scope)
    {
      var domain = linearDomains[domainName];
      foreach (Variable v in scope)
      {
        Expr expr = new NAryExpr(Token.NoToken, new FunctionCall(domain.collectors[v.TypedIdent.Type]),
          new List<Expr> {Expr.Ident(v)});
        expr.Resolve(new ResolutionContext(null));
        expr.Typecheck(new TypecheckingContext(null));
        yield return expr;
      }
    }

    public Expr DisjointnessExprForPermissions(string domainName, IEnumerable<Expr> permissionsExprs)
    {
      Expr expr = Expr.True;
      if (permissionsExprs.Count() > 1)
      {
        int count = 0;
        List<Expr> subsetExprs = new List<Expr>();
        LinearDomain domain = linearDomains[domainName];
        BoundVariable partition = new BoundVariable(Token.NoToken,
          new TypedIdent(Token.NoToken, $"partition_{domainName}", domain.mapTypeInt));
        foreach (Expr e in permissionsExprs)
        {
          subsetExprs.Add(SubsetExpr(domain, e, partition, count));
          count++;
        }

        expr = new ExistsExpr(Token.NoToken, new List<Variable> {partition}, Expr.And(subsetExprs));
      }

      expr.Resolve(new ResolutionContext(null));
      expr.Typecheck(new TypecheckingContext(null));
      return expr;
    }

    public Expr UnionExprForPermissions(string domainName, IEnumerable<Expr> permissionExprs)
    {
      var domain = linearDomains[domainName];
      var expr = new NAryExpr(Token.NoToken, new FunctionCall(domain.mapConstBool), new Expr[] {Expr.False});
      foreach (Expr e in permissionExprs)
      {
        expr = new NAryExpr(Token.NoToken, new FunctionCall(domain.mapOrBool), new List<Expr> {e, expr});
      }

      expr.Resolve(new ResolutionContext(null));
      expr.Typecheck(new TypecheckingContext(null));
      return expr;
    }

    private Expr SubsetExpr(LinearDomain domain, Expr ie, Variable partition, int partitionCount)
    {
      Expr e = ExprHelper.FunctionCall(domain.mapConstInt, Expr.Literal(partitionCount));
      e = ExprHelper.FunctionCall(domain.mapEqInt, Expr.Ident(partition), e);
      e = ExprHelper.FunctionCall(domain.mapImpBool, ie, e);
      e = Expr.Eq(e, ExprHelper.FunctionCall(domain.mapConstBool, Expr.True));
      return e;
    }

    #endregion

    #region Linearity Invariant Checker

    public static void AddCheckers(CivlTypeChecker civlTypeChecker, List<Declaration> decls)
    {
      foreach (var action in Enumerable.Concat<Action>(civlTypeChecker.procToAtomicAction.Values,
        civlTypeChecker.procToIntroductionAction.Values))
      {
        AddChecker(civlTypeChecker, action, decls);
      }
    }

    private static void AddChecker(CivlTypeChecker civlTypeChecker, Action action, List<Declaration> decls)
    {
      var linearTypeChecker = civlTypeChecker.linearTypeChecker;
      // Note: The implementation should be used as the variables in the
      //       gate are bound to implementation and not to the procedure.
      Implementation impl = action.impl;
      List<Variable> inputs = impl.InParams;
      List<Variable> outputs = impl.OutParams;

      // Linear out vars
      List<Variable> outVars;
      {
        LinearKind[] validKinds = {LinearKind.LINEAR, LinearKind.LINEAR_OUT};
        outVars = inputs.Union(outputs).Union(action.modifiedGlobalVars)
          .Where(x => validKinds.Contains(linearTypeChecker.FindLinearKind(x)))
          .ToList();
      }

      if (outVars.Count == 0) return;

      // Linear in vars
      List<Variable> inVars;
      {
        LinearKind[] validKinds = {LinearKind.LINEAR, LinearKind.LINEAR_IN};
        inVars = inputs.Union(action.modifiedGlobalVars)
          .Where(x => validKinds.Contains(linearTypeChecker.FindLinearKind(x)))
          .ToList();
      }

      List<Requires> requires = action.gate.Select(a => new Requires(false, a.Expr)).ToList();
      List<Ensures> ensures = new List<Ensures>();

      // Generate linearity checks
      IEnumerable<string> domainNames = outVars.Select(linearTypeChecker.FindDomainName).Distinct();
      foreach (var domainName in domainNames)
      {
        LinearDomain domain = linearTypeChecker.linearDomains[domainName];
        Expr inMultiset = linearTypeChecker.PermissionMultiset(domain, inVars, true);
        Expr outMultiset = linearTypeChecker.PermissionMultiset(domain, outVars);
        Expr subsetExpr = ExprHelper.FunctionCall(domain.mapLeInt, outMultiset, inMultiset);
        Expr eqExpr = Expr.Eq(subsetExpr, ExprHelper.FunctionCall(domain.mapConstBool, Expr.True));

        Ensures ensureCheck = new Ensures(action.proc.tok, false, eqExpr, null);
        ensureCheck.ErrorData = $"Linearity invariant for domain {domainName} is not preserved by {action.proc.Name}.";
        ResolutionContext rc = new ResolutionContext(null);
        rc.StateMode = ResolutionContext.State.Two;
        ensureCheck.Resolve(rc);
        ensureCheck.Typecheck(new TypecheckingContext(null));
        ensures.Add(ensureCheck);
      }

      // Create blocks
      List<Block> blocks = new List<Block>()
      {
        new Block(
          Token.NoToken,
          "init",
          new List<Cmd> {CmdHelper.CallCmd(action.proc, inputs, outputs)},
          CmdHelper.ReturnCmd)
      };

      // Create the whole check procedure
      string checkerName = civlTypeChecker.AddNamePrefix($"LinearityChecker_{action.proc.Name}");
      Procedure linCheckerProc = new Procedure(Token.NoToken, checkerName, new List<TypeVariable>(),
        inputs, outputs, requires, action.proc.Modifies, ensures);
      Implementation linCheckImpl = new Implementation(Token.NoToken, checkerName,
        new List<TypeVariable>(), inputs, outputs, new List<Variable> { }, blocks);
      linCheckImpl.Proc = linCheckerProc;
      decls.Add(linCheckImpl);
      decls.Add(linCheckerProc);
    }

    private Expr PermissionMultiset(LinearDomain domain, IEnumerable<Variable> vars, bool useOldExpr = false)
    {
      Expr mapConstInt0 = ExprHelper.FunctionCall(domain.mapConstInt, Expr.Literal(0));
      Expr mapConstInt1 = ExprHelper.FunctionCall(domain.mapConstInt, Expr.Literal(1));

      var terms = vars.Where(x => FindDomainName(x) == domain.domainName).Select(x =>
        ExprHelper.FunctionCall(domain.mapIteInt,
          CollectedLinearVariable(x, domain.collectors[x.TypedIdent.Type], useOldExpr),
          mapConstInt1,
          mapConstInt0)).ToList<Expr>();

      if (terms.Count == 0)
        return mapConstInt0;
      return terms.Aggregate((x, y) => ExprHelper.FunctionCall(domain.mapAddInt, x, y));
    }

    private Expr CollectedLinearVariable(Variable v, Function collector, bool useOldExpr = false)
    {
      Expr arg = Expr.Ident(v);
      if (useOldExpr)
        arg = ExprHelper.Old(arg);
      return ExprHelper.FunctionCall(collector, arg);
    }

    #endregion

    #region Annotation Eraser

    public void EraseLinearAnnotations()
    {
      new LinearTypeEraser().VisitProgram(program);
    }

    public class LinearTypeEraser : ReadOnlyVisitor
    {
      public override Variable VisitVariable(Variable node)
      {
        CivlAttributes.RemoveLinearAttributes(node);
        return base.VisitVariable(node);
      }

      public override Function VisitFunction(Function node)
      {
        CivlAttributes.RemoveLinearAttributes(node);
        return base.VisitFunction(node);
      }
    }

    #endregion
  }
}
