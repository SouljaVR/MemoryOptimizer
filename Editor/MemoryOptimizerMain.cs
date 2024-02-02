using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;
using JeTeeS.TES.HelperFunctions;
using static JeTeeS.TES.HelperFunctions.TESHelperFunctions;
using YamlDotNet.Core.Tokens;

namespace JeTeeS.MemoryOptimizer
{
    public static class MemoryOptimizerMain
    {
        public class MemoryOptimizerListData
        {
            public VRCExpressionParameters.Parameter param;
            public bool selected = false;
            public bool willBeOptimized = false;

            public MemoryOptimizerListData(VRCExpressionParameters.Parameter parameter, bool isSelected, bool willOptimize)
            {
                param = parameter;
                selected = isSelected;
                willBeOptimized = willOptimize;
            }
        }

        public class ParamDriversAndStates
        {
            public VRCAvatarParameterDriver paramDriver = ScriptableObject.CreateInstance<VRCAvatarParameterDriver>();
            public List<AnimatorState> states = new List<AnimatorState>();
        }

        private const string prefix = "MemOpt_";
        private const string syncingLayerName = prefix + "Syncing Layer";
        private const string syncingLayerIdentifier = prefix + "Syncer";
        private const string mainBlendTreeIdentifier = prefix + "MainBlendTree";
        private const string mainBlendTreeLayerName = prefix + "Main BlendTree";
        private const string smoothingAmountParamName = prefix + "ParamSmoothing";
        private const string smoothedVerSuffix = "_S";
        private const string SmoothingTreeName = "SmoothingParentTree";
        private const string DifferentialTreeName = "DifferentialParentTree";
        private const string Differentialsuffix = "_Delta";
        private const string constantOneName = prefix + "ConstantOne";
        private const string indexerParamName = prefix + "Indexer ";
        private const string boolSyncerParamName = prefix + "BoolSyncer ";
        private const string intNFloatSyncerParamName = prefix + "IntNFloatSyncer ";
        private const string oneFrameBufferAnimName = prefix + "OneFrameBuffer";
        private const string oneSecBufferAnimName = prefix + "OneSecBuffer";
        private const float changeSensitivity = 0.05f;

        private class MemoryOptimizerState
        {
            public AnimationClip oneSecBuffer;
            public AnimationClip oneFrameBuffer;
            public VRCAvatarDescriptor avatar;
            public AnimatorController FXController;
            public VRCExpressionParameters expressionParameters;
            public AnimatorStateMachine localStateMachine, remoteStateMachine;
            public List<MemoryOptimizerListData> boolsToOptimize, intsNFloatsToOptimize;
            public List<AnimatorControllerParameter> boolsDifferentials, intsNFloatsDifferentials, boolsNIntsWithCopies;
            public List<AnimatorState> localSetStates, localResetStates, remoteSetStates;
            public List<ParamDriversAndStates> localSettersParameterDrivers, remoteSettersParameterDrivers;
            public AnimatorControllerLayer syncingLayer;
        }

        public static bool FindInstallation(AnimatorController controller)
        {
            if (controller == null) return false;
            if (controller.FindHiddenIdentifier(syncingLayerIdentifier).Count == 1) return true;
            if (controller.FindHiddenIdentifier(mainBlendTreeIdentifier).Count == 1) return true;

            return false;
        }

        public static void InstallMemOpt(VRCAvatarDescriptor avatarIn, AnimatorController fxLayer, VRCExpressionParameters expressionParameters, List<MemoryOptimizerListData> boolsToOptimize, List<MemoryOptimizerListData> intsNFloatsToOptimize, int syncSteps, float stepDelay, bool generateChangeCheck, int wdOption, string mainFilePath)
        {
            string generatedAssetsFilePath = mainFilePath + "GeneratedAssets/";
            ReadyPath(generatedAssetsFilePath);

            MemoryOptimizerState optimizerState = new MemoryOptimizerState()
            {
                avatar = avatarIn,
                FXController = fxLayer,
                expressionParameters = expressionParameters,
                boolsToOptimize = boolsToOptimize,
                intsNFloatsToOptimize = intsNFloatsToOptimize,
                boolsNIntsWithCopies = new List<AnimatorControllerParameter>()
            };

            /*Debug stuff
            Debug.Log("Optimizing Params...");
            foreach (MemoryOptimizerListData listData in boolsToOptimize) Debug.Log("Optimizing: " + listData.param.name + " that is the type: " + listData.param.valueType.ToString());
            foreach (MemoryOptimizerListData listData in intsNFloatsToOptimize) Debug.Log("Optimizing: " + listData.param.name + " that is the type: " + listData.param.valueType.ToString());
            */

            optimizerState.syncingLayer = new AnimatorControllerLayer
            {
                defaultWeight = 1,
                name = syncingLayerName,
                stateMachine = new AnimatorStateMachine
                {
                    hideFlags = HideFlags.HideInHierarchy,
                    name = syncingLayerName,
                    anyStatePosition = new Vector3(20, 20, 0),
                    entryPosition = new Vector3(20, 50, 0)
                }
            };
            optimizerState.syncingLayer.stateMachine.AddHiddenIdentifier(syncingLayerIdentifier);

            (optimizerState.oneFrameBuffer, optimizerState.oneSecBuffer) = bufferAnims(generatedAssetsFilePath);

            fxLayer.AddUniqueParam("IsLocal", AnimatorControllerParameterType.Bool);

            AnimatorControllerParameter constantOneParam = fxLayer.AddUniqueParam(constantOneName, AnimatorControllerParameterType.Float, 1);
            fxLayer.AddUniqueParam(smoothingAmountParamName);

            string syncStepsBinary = (syncSteps - 1).DecimalToBinary().ToString();
            for (int i = 0; i < syncStepsBinary.Count(); i++)
            {
                AddUniqueSyncedParamToController(indexerParamName + (i + 1).ToString(), fxLayer, expressionParameters, AnimatorControllerParameterType.Bool, VRCExpressionParameters.ValueType.Bool);
            }

            for (int j = 0; j < boolsToOptimize.Count / syncSteps; j++)
            {
                AddUniqueSyncedParamToController(boolSyncerParamName + j, optimizerState.FXController, optimizerState.expressionParameters, AnimatorControllerParameterType.Bool, VRCExpressionParameters.ValueType.Bool);
            }
            for (int j = 0; j < intsNFloatsToOptimize.Count / syncSteps; j++)
            {
                AddUniqueSyncedParamToController(intNFloatSyncerParamName + j, optimizerState.FXController, optimizerState.expressionParameters, AnimatorControllerParameterType.Bool, VRCExpressionParameters.ValueType.Bool);
            }

            CreateLocalRemoteSplit(optimizerState);

            if(generateChangeCheck) { GenerateDeltas(optimizerState, generatedAssetsFilePath); }
            
            CreateStates(optimizerState, syncSteps, stepDelay, generateChangeCheck);

            AnimatorState localEntryState = optimizerState.localStateMachine.AddState("Entry", new Vector3(0, 100, 0));
            localEntryState.hideFlags = HideFlags.HideInHierarchy;
            localEntryState.motion = optimizerState.oneFrameBuffer;
            localEntryState.writeDefaultValues = false;

            //add transition from local entry to 1st set value
            localEntryState.AddTransition(new AnimatorStateTransition { destinationState = optimizerState.localSetStates[0], exitTime = 0, hasExitTime = true, hasFixedDuration = true, duration = 0f, hideFlags = HideFlags.HideInHierarchy });

            CreateTransitions(optimizerState, localEntryState, syncSteps, stepDelay, generateChangeCheck);

            CreateParameterDrivers(optimizerState, syncSteps, generateChangeCheck);

            bool setWD = true;
            if (wdOption == 0) 
            {
                int foundWD = fxLayer.FindWDInController();
                if (foundWD == -1) setWD = true;
                if (foundWD == 0) setWD = false;
                if (foundWD == 1) setWD = true;
            }
            else if (wdOption == 1)
            {
                setWD = false;
            }
            else
            {
                setWD = true;
            }
            foreach (var state in optimizerState.syncingLayer.FindAllStatesInLayer()) state.state.writeDefaultValues = setWD;

            optimizerState.FXController.AddLayer(optimizerState.syncingLayer);
            optimizerState.FXController.SaveUnsavedAssetsToController();

            foreach (var param in boolsToOptimize) { param.param.networkSynced = false; }
            foreach (var param in intsNFloatsToOptimize) { param.param.networkSynced = false; }
            EditorUtility.SetDirty(expressionParameters);

            AssetDatabase.SaveAssets();

            SetupParameterDrivers(optimizerState);
        }

        private static void GenerateDeltas(MemoryOptimizerState optimizerState, string generatedAssetsFilePath)
        {
            List<MemoryOptimizerListData> boolsToOptimize = optimizerState.boolsToOptimize;
            List<MemoryOptimizerListData> intsNFloatsToOptimize = optimizerState.intsNFloatsToOptimize;

            List<AnimatorControllerParameter> boolsDifferentials = new List<AnimatorControllerParameter>();
            List<AnimatorControllerParameter> intsNFloatsDifferentials = new List<AnimatorControllerParameter>();
            //Add smoothed ver of every param in the list
            foreach (MemoryOptimizerListData param in boolsToOptimize)
            {
                List<AnimatorControllerParameter> paramMatches = optimizerState.FXController.parameters.Where(x => x.name == param.param.name).ToList();
                AnimatorControllerParameter paramMatch = paramMatches[0];
                if (paramMatch.type == AnimatorControllerParameterType.Bool)
                {
                    AnimatorControllerParameter paramCopy = optimizerState.FXController.AddUniqueParam(prefix + paramMatch.name + "_Copy");
                    optimizerState.boolsNIntsWithCopies.Add(paramMatch);
                    AnimatorControllerParameter smoothedParam = paramCopy.AddSmoothedVer(0, 1, optimizerState.FXController, prefix + paramCopy.name + smoothedVerSuffix, generatedAssetsFilePath, smoothingAmountParamName, mainBlendTreeIdentifier, mainBlendTreeLayerName, SmoothingTreeName, constantOneName);
                    boolsDifferentials.Add(AddParamDifferential(paramCopy, smoothedParam, optimizerState.FXController, generatedAssetsFilePath, 0, 1, prefix + paramCopy.name + Differentialsuffix, mainBlendTreeIdentifier, mainBlendTreeLayerName, DifferentialTreeName, constantOneName));
                }
                else if (paramMatch.type == AnimatorControllerParameterType.Float)
                {
                    AnimatorControllerParameter smoothedParam = paramMatch.AddSmoothedVer(0, 1, optimizerState.FXController, prefix + paramMatch.name + smoothedVerSuffix, generatedAssetsFilePath, smoothingAmountParamName, mainBlendTreeIdentifier, mainBlendTreeLayerName, SmoothingTreeName, constantOneName);
                    boolsDifferentials.Add(AddParamDifferential(paramMatch, smoothedParam, optimizerState.FXController, generatedAssetsFilePath, 0, 1, prefix + paramMatch.name + Differentialsuffix, mainBlendTreeIdentifier, mainBlendTreeLayerName, DifferentialTreeName, constantOneName));
                }
                else
                {
                    Debug.LogError("Param " + param.param.name + "is not bool or float!");
                }
            }

            foreach (MemoryOptimizerListData param in intsNFloatsToOptimize)
            {
                List<AnimatorControllerParameter> paramMatches = optimizerState.FXController.parameters.Where(x => x.name == param.param.name).ToList();
                AnimatorControllerParameter paramMatch = paramMatches[0];
                if (paramMatch.type == AnimatorControllerParameterType.Int)
                {
                    AnimatorControllerParameter paramCopy = optimizerState.FXController.AddUniqueParam(prefix + paramMatch.name + "_Copy");
                    optimizerState.boolsNIntsWithCopies.Add(paramMatch);
                    AnimatorControllerParameter smoothedParam = paramCopy.AddSmoothedVer(0, 1, optimizerState.FXController, prefix + paramCopy.name + smoothedVerSuffix, generatedAssetsFilePath, smoothingAmountParamName, mainBlendTreeIdentifier, mainBlendTreeLayerName, SmoothingTreeName, constantOneName);
                    intsNFloatsDifferentials.Add(AddParamDifferential(paramCopy, smoothedParam, optimizerState.FXController, generatedAssetsFilePath, 0, 1, prefix + paramCopy.name + Differentialsuffix, mainBlendTreeIdentifier, mainBlendTreeLayerName, DifferentialTreeName, constantOneName));
                }
                else if (paramMatch.type == AnimatorControllerParameterType.Float)
                {
                    AnimatorControllerParameter smoothedParam = paramMatch.AddSmoothedVer(-1, 1, optimizerState.FXController, prefix + paramMatch.name + smoothedVerSuffix, generatedAssetsFilePath, smoothingAmountParamName, mainBlendTreeIdentifier, mainBlendTreeLayerName, SmoothingTreeName, constantOneName);
                    intsNFloatsDifferentials.Add(AddParamDifferential(paramMatch, smoothedParam, optimizerState.FXController, generatedAssetsFilePath, -1, 1, prefix + paramMatch.name + Differentialsuffix, mainBlendTreeIdentifier, mainBlendTreeLayerName, DifferentialTreeName, constantOneName));
                }
                else
                {
                    Debug.LogError("Param " + param.param.name + "is not bool or float!");
                }


            }

            optimizerState.boolsDifferentials = boolsDifferentials;
            optimizerState.intsNFloatsDifferentials = intsNFloatsDifferentials;
        }

        private static void CreateTransitions(MemoryOptimizerState optimizerState, AnimatorState localEntryState, int syncSteps, float stepDelay, bool generateChangeCheck)
        {
            List<MemoryOptimizerListData> boolsToOptimize = optimizerState.boolsToOptimize;
            List<MemoryOptimizerListData> intsNFloatsToOptimize = optimizerState.intsNFloatsToOptimize;
            List<AnimatorState> localSetStates = optimizerState.localSetStates;
            List<AnimatorState> remoteSetStates = optimizerState.remoteSetStates;
            List<AnimatorState> localResetStates = optimizerState.localResetStates;
            List<AnimatorControllerParameter> boolsDifferentials = optimizerState.boolsDifferentials;
            List<AnimatorControllerParameter> intsNFloatsDifferentials = optimizerState.intsNFloatsDifferentials;
            AnimatorStateMachine remoteStateMachine = optimizerState.remoteStateMachine;
            AnimatorStateMachine localStateMachine = optimizerState.localStateMachine;

            string syncStepsBinary = (syncSteps - 1).DecimalToBinary().ToString();

            AnimatorState localValueChangedState = null;
            if (generateChangeCheck)
            {
                localValueChangedState = localStateMachine.AddState("Value Changed", new Vector3(0, 600, 0));
                localValueChangedState.hideFlags = HideFlags.HideInHierarchy;
                localValueChangedState.motion = optimizerState.oneFrameBuffer;
                localValueChangedState.writeDefaultValues = false;
                localValueChangedState.AddTransition(new AnimatorStateTransition()
                {
                    destinationState = localEntryState,
                    exitTime = 1,
                    hasExitTime = true,
                    hasFixedDuration = true,
                    duration = 0f,
                    hideFlags = HideFlags.HideInHierarchy
                });
            }

            AnimatorState waitForIndexer = remoteStateMachine.AddState("WaitForIndexer", new Vector3(0, 400, 0));
            waitForIndexer.hideFlags = HideFlags.HideInHierarchy;
            waitForIndexer.writeDefaultValues = false;
            waitForIndexer.motion = optimizerState.oneFrameBuffer;

            for (int i = 0; i < syncSteps; i++)
            {
                string currentIndex = i.DecimalToBinary().ToString();
                while (currentIndex.Length < syncStepsBinary.Length)
                {
                    currentIndex = "0" + currentIndex;
                }

                AnimatorStateTransition toSetterTransition = new AnimatorStateTransition()
                {
                    destinationState = remoteSetStates[i],
                    exitTime = 0,
                    hasExitTime = true,
                    hasFixedDuration = true,
                    duration = 0f,
                    hideFlags = HideFlags.HideInHierarchy
                };


                //Make a list of transitions that go to the "wait" state
                List<AnimatorStateTransition> toWaitTransitions = new List<AnimatorStateTransition>();

                //loop through each character of the binary number
                for (int j = 1; j <= currentIndex.Length; j++)
                {
                    bool isZero = currentIndex[currentIndex.Length - j].ToString() == "0";
                    toSetterTransition.AddCondition(isZero ? AnimatorConditionMode.IfNot : AnimatorConditionMode.If, 0, indexerParamName + j);
                    toWaitTransitions.Add(new AnimatorStateTransition
                    {
                        destinationState = waitForIndexer,
                        exitTime = 0,
                        hasExitTime = false,
                        hasFixedDuration = true,
                        duration = 0f,
                        hideFlags = HideFlags.HideInHierarchy
                    });
                    toWaitTransitions.Last().AddCondition(isZero ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, 0, indexerParamName + j);
                }

                if (generateChangeCheck)
                {
                    void SetupLocalResetStateTransitions(string differentialName, int optimizeIndex, bool isBool)
                    {
                        //add transitions from value changed state to appropriate reset state
                        AnimatorStateTransition transition = new AnimatorStateTransition
                        {
                            destinationState = localResetStates[i],
                            exitTime = 0,
                            hasExitTime = false,
                            hasFixedDuration = true,
                            duration = 0f,
                            hideFlags = HideFlags.HideInHierarchy
                        };
                        transition.AddCondition(AnimatorConditionMode.Greater, changeSensitivity, differentialName);

                        localValueChangedState.AddTransition(transition);
                    }

                    for (int j = 0; j < boolsToOptimize.Count / syncSteps; j++)
                    {
                        string differentialName = boolsDifferentials[i * (boolsToOptimize.Count() / syncSteps) + j].name;
                        SetupLocalResetStateTransitions(differentialName, j, true);
                    }

                    for (int j = 0; j < intsNFloatsToOptimize.Count / syncSteps; j++)
                    {
                        string differentialName = intsNFloatsDifferentials[i * (intsNFloatsToOptimize.Count() / syncSteps) + j].name;
                        SetupLocalResetStateTransitions(differentialName, j, false);
                    }
                }

                //add the transitions from remote set states to the wait state
                foreach (AnimatorStateTransition transition in toWaitTransitions)
                {
                    remoteSetStates[i].AddTransition(transition);
                }

                //add transition from wait state to current set state
                waitForIndexer.AddTransition(toSetterTransition);
            }

            void SetupLocalSetStateTransition(MemoryOptimizerListData memoryOptimizerListData, int stateIndex, bool isBool)
            {
                AnimatorStateTransition transition = new AnimatorStateTransition
                {
                    destinationState = localValueChangedState,
                    exitTime = 0,
                    hasExitTime = false,
                    hasFixedDuration = true,
                    duration = 0f,
                    hideFlags = HideFlags.HideInHierarchy
                };
                transition.AddCondition(AnimatorConditionMode.Greater, changeSensitivity, (isBool ? optimizerState.boolsDifferentials : optimizerState.intsNFloatsDifferentials).First(x => x.name.Contains(memoryOptimizerListData.param.name)).name);
                localSetStates[stateIndex].AddTransition(transition);
            }

            for (int i = 0; i < localSetStates.Count; i++)
            {
                if (generateChangeCheck)
                {
                    //add transitions from the set states to value changed state
                    foreach (MemoryOptimizerListData param in boolsToOptimize)
                    {
                        SetupLocalSetStateTransition(param, i, true);
                    }
                    foreach (MemoryOptimizerListData param in intsNFloatsToOptimize)
                    {
                        SetupLocalSetStateTransition(param, i, false);
                    }
                }
                
                localSetStates[i].AddTransition(new AnimatorStateTransition() { destinationState = localSetStates[(i + 1) % localSetStates.Count], exitTime = stepDelay, hasExitTime = true, hasFixedDuration = true, duration = 0f, hideFlags = HideFlags.HideInHierarchy });
            }
        }

        private static void CreateParameterDrivers(MemoryOptimizerState optimizerState, int syncSteps, bool generateChangeCheck)
        {
            List<AnimatorState> localSetStates = optimizerState.localSetStates;
            List<AnimatorState> localResetStates = optimizerState.localResetStates;
            List<AnimatorState> remoteSetStates = optimizerState.remoteSetStates;
            List<MemoryOptimizerListData> boolsToOptimize = optimizerState.boolsToOptimize;
            List<MemoryOptimizerListData> intsNFloatsToOptimize = optimizerState.intsNFloatsToOptimize;

            List<ParamDriversAndStates> localSettersParameterDrivers = new List<ParamDriversAndStates>();
            List<ParamDriversAndStates> remoteSettersParameterDrivers = new List<ParamDriversAndStates>();
            string syncStepsBinary = (syncSteps - 1).DecimalToBinary().ToString();
            for (int i = 0; i < syncSteps; i++)
            {
                string currentIndex = i.DecimalToBinary().ToString();
                while (currentIndex.Length < syncStepsBinary.Length)
                {
                    currentIndex = "0" + currentIndex;
                }

                localSettersParameterDrivers.Add(new ParamDriversAndStates());
                localSettersParameterDrivers.Last().states.Add(localSetStates[i]);
                if (generateChangeCheck)
                {
                    localSettersParameterDrivers.Last().states.Add(localResetStates[i]);

                    foreach(AnimatorControllerParameter param in optimizerState.boolsNIntsWithCopies)
                    {
                        localSettersParameterDrivers.Last().paramDriver.parameters.Add(new VRC_AvatarParameterDriver.Parameter() { name = prefix + param.name + "_Copy", source = param.name, type = VRC_AvatarParameterDriver.ChangeType.Copy });
                    }
                }

                remoteSettersParameterDrivers.Add(new ParamDriversAndStates());
                remoteSettersParameterDrivers.Last().states.Add(remoteSetStates[i]);

                //Make a list of transitions that go to the "wait" state
                List<AnimatorStateTransition> toWaitTransitions = new List<AnimatorStateTransition>();

                //loop through each character of the binary number
                for (int j = 1; j <= currentIndex.Length; j++)
                {
                    int value = currentIndex[currentIndex.Length - j].ToString() == "0" ? 0 : 1;
                    localSettersParameterDrivers.Last().paramDriver.parameters.Add(new VRC_AvatarParameterDriver.Parameter() { name = indexerParamName + j, value = value, type = VRC_AvatarParameterDriver.ChangeType.Set });
                    remoteSettersParameterDrivers.Last().paramDriver.parameters.Add(new VRC_AvatarParameterDriver.Parameter() { name = indexerParamName + j, value = value, type = VRC_AvatarParameterDriver.ChangeType.Set });
                }

                for (int j = 0; j < boolsToOptimize.Count / syncSteps; j++)
                {
                    VRCExpressionParameters.Parameter param = boolsToOptimize.ElementAt(i * (boolsToOptimize.Count() / syncSteps) + j).param;
                    localSettersParameterDrivers.Last().paramDriver.parameters.Add(new VRC_AvatarParameterDriver.Parameter()
                    {
                        name = boolSyncerParamName + j,
                        source = param.name,
                        type = VRC_AvatarParameterDriver.ChangeType.Copy
                    });
                    remoteSettersParameterDrivers.Last().paramDriver.parameters.Add(new VRC_AvatarParameterDriver.Parameter()
                    {
                        name = param.name,
                        source = boolSyncerParamName + j,
                        type = VRC_AvatarParameterDriver.ChangeType.Copy
                    });
                }

                for (int j = 0; j < intsNFloatsToOptimize.Count / syncSteps; j++)
                {
                    VRCExpressionParameters.Parameter param = intsNFloatsToOptimize.ElementAt(i * (intsNFloatsToOptimize.Count() / syncSteps) + j).param;
                    if (param.valueType == VRCExpressionParameters.ValueType.Int)
                    {
                        localSettersParameterDrivers.Last().paramDriver.parameters.Add(new VRC_AvatarParameterDriver.Parameter()
                        {
                            name = intNFloatSyncerParamName + j,
                            source = param.name,
                            type = VRC_AvatarParameterDriver.ChangeType.Copy
                        });
                        remoteSettersParameterDrivers.Last().paramDriver.parameters.Add(
                            new VRC_AvatarParameterDriver.Parameter()
                            {
                                name = param.name,
                                source = intNFloatSyncerParamName + j,
                                type = VRC_AvatarParameterDriver.ChangeType.Copy
                            });
                    }
                    else if (param.valueType == VRCExpressionParameters.ValueType.Float)
                    {
                        localSettersParameterDrivers.Last().paramDriver.parameters.Add(new VRC_AvatarParameterDriver.Parameter()
                        {
                            name = intNFloatSyncerParamName + j,
                            source = param.name,
                            type = VRC_AvatarParameterDriver.ChangeType.Copy,
                            convertRange = true,
                            destMin = 0,
                            destMax = 255,
                            sourceMin = 0,
                            sourceMax = 1
                        });
                        remoteSettersParameterDrivers.Last().paramDriver.parameters.Add(
                            new VRC_AvatarParameterDriver.Parameter()
                            {
                                name = param.name,
                                source = intNFloatSyncerParamName + j,
                                type = VRC_AvatarParameterDriver.ChangeType.Copy,
                                convertRange = true,
                                destMin = 0,
                                destMax = 1,
                                sourceMin = 0,
                                sourceMax = 255
                            });
                    }
                    else
                    {
                        Debug.LogError(param.name + " is not an int or a float!");
                    }
                }
            }

            optimizerState.localSettersParameterDrivers = localSettersParameterDrivers;
            optimizerState.remoteSettersParameterDrivers = remoteSettersParameterDrivers;
        }

        private static void CreateStates(MemoryOptimizerState optimizerState, int syncSteps, float stepDelay, bool generateChangeCheck)
        {
            string syncStepsBinary = (syncSteps - 1).DecimalToBinary().ToString();
            AnimatorStateMachine localStateMachine = optimizerState.localStateMachine;
            AnimatorStateMachine remoteStateMachine = optimizerState.remoteStateMachine;

            List<AnimatorState> localSetStates = new List<AnimatorState>();
            List<AnimatorState> localResetStates = new List<AnimatorState>();
            List<AnimatorState> remoteSetStates = new List<AnimatorState>();
            for (int i = 0; i < syncSteps; i++)
            {
                //convert i to binary so it can be used for the binary counter
                string currentIndex = i.DecimalToBinary().ToString();
                while (currentIndex.Length < syncStepsBinary.Length)
                {
                    currentIndex = "0" + currentIndex;
                }

                //add the local set and reset states
                localSetStates.Add(localStateMachine.AddState("Set Value " + (i + 1), AngleRadiusToPos(((float)i / syncSteps + 0.5f) * (float)Math.PI * 2f, 400f, new Vector3(0, 600, 0))));
                localSetStates.Last().hideFlags = HideFlags.HideInHierarchy;
                localSetStates.Last().writeDefaultValues = false;
                localSetStates.Last().motion = optimizerState.oneSecBuffer;

                if (generateChangeCheck)
                {
                    localResetStates.Add(localStateMachine.AddState("Reset Change Check " + (i + 1), AngleRadiusToPos(((float)i / syncSteps + 0.5f) * (float)Math.PI * 2f + ((float)Math.PI * 0.25f), 480f, new Vector3(0, 600, 0))));
                    localResetStates.Last().hideFlags = HideFlags.HideInHierarchy;
                    localResetStates.Last().motion = optimizerState.oneSecBuffer;
                    localResetStates.Last().writeDefaultValues = false;

                    localResetStates.Last().AddTransition(new AnimatorStateTransition()
                    {
                        destinationState = localSetStates.Last(),
                        exitTime = stepDelay / 4,
                        hasExitTime = true,
                        hasFixedDuration = true,
                        duration = 0f,
                        hideFlags = HideFlags.HideInHierarchy
                    });
                }

                //add the remote set states
                remoteSetStates.Add(remoteStateMachine.AddState("Set values for index " + (i + 1), AngleRadiusToPos(((float)i / syncSteps + 0.5f) * (float)Math.PI * 2f, 250f, new Vector3(0, 400, 0))));
                remoteSetStates.Last().hideFlags = HideFlags.HideInHierarchy;
                remoteSetStates.Last().motion = optimizerState.oneFrameBuffer;
                remoteSetStates.Last().writeDefaultValues = false;
            }

            optimizerState.localSetStates = localSetStates;
            optimizerState.localResetStates = localResetStates;
            optimizerState.remoteSetStates = remoteSetStates;
        }

        private static (AnimationClip oneFrameBuffer, AnimationClip oneSecBuffer) bufferAnims(string generatedAssetsFilePath)
        {
            //create and overwrite single frame buffer animation
            AnimationClip oneFrameBuffer = new AnimationClip() { name = oneFrameBufferAnimName, };
            AnimationCurve oneFrameBufferCurve = new AnimationCurve();
            oneFrameBufferCurve.AddKey(0, 0);
            oneFrameBufferCurve.AddKey(1 / 60f, 1);
            oneFrameBuffer.SetCurve("", typeof(GameObject), "DO NOT CHANGE THIS ANIMATION", oneFrameBufferCurve);
            AssetDatabase.DeleteAsset(generatedAssetsFilePath + oneFrameBuffer.name + ".anim");
            AssetDatabase.CreateAsset(oneFrameBuffer, generatedAssetsFilePath + oneFrameBuffer.name + ".anim");

            //create and overwrite one second buffer animation
            AnimationClip oneSecBuffer = new AnimationClip() { name = oneSecBufferAnimName, };
            AnimationCurve oneSecBufferCurve = new AnimationCurve();
            oneSecBufferCurve.AddKey(0, 0);
            oneSecBufferCurve.AddKey(1, 1);
            oneSecBuffer.SetCurve("", typeof(GameObject), "DO NOT CHANGE THIS ANIMATION", oneSecBufferCurve);
            AssetDatabase.DeleteAsset(generatedAssetsFilePath + oneSecBuffer.name + ".anim");
            AssetDatabase.CreateAsset(oneSecBuffer, generatedAssetsFilePath + oneSecBuffer.name + ".anim");
            return (oneFrameBuffer, oneSecBuffer);
        }

        private static void SetupParameterDrivers(MemoryOptimizerState optimizerState)
        {
            List<ParamDriversAndStates> localSettersParameterDrivers = optimizerState.localSettersParameterDrivers;
            List<ParamDriversAndStates> remoteSettersParameterDrivers = optimizerState.remoteSettersParameterDrivers;
            List<AnimatorState> localSetStates = optimizerState.localSetStates;
            List<AnimatorState> localResetStates = optimizerState.localResetStates;

            foreach (ParamDriversAndStates driver in localSettersParameterDrivers)
            {
                foreach (AnimatorState state in driver.states)
                {
                    VRCAvatarParameterDriver temp = state.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
                    temp.parameters = driver.paramDriver.parameters.ToList();
                }
            }

            foreach (ParamDriversAndStates driver in remoteSettersParameterDrivers)
            {
                foreach (AnimatorState state in driver.states)
                {
                    VRCAvatarParameterDriver temp = state.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
                    temp.parameters = driver.paramDriver.parameters;
                }
            }

            foreach (AnimatorState state in localSetStates)
            {
                VRCAvatarParameterDriver temp = (VRCAvatarParameterDriver)state.behaviours[0];
                temp.parameters.Add(new VRC_AvatarParameterDriver.Parameter() { name = smoothingAmountParamName, type = VRC_AvatarParameterDriver.ChangeType.Set, value = 0 });
            }

            foreach (AnimatorState state in localResetStates)
            {
                VRCAvatarParameterDriver temp = (VRCAvatarParameterDriver)state.behaviours[0];
                temp.parameters.Add(new VRC_AvatarParameterDriver.Parameter() { name = smoothingAmountParamName, type = VRC_AvatarParameterDriver.ChangeType.Set, value = 1 });
            }
        }

        private static void CreateLocalRemoteSplit(MemoryOptimizerState optimizerState)
        {
            AnimatorControllerLayer syncingLayer = optimizerState.syncingLayer;
            AnimatorState localRemoteSplitState =
                syncingLayer.stateMachine.AddState("Local/Remote split", position: new Vector3(0, 100, 0));
            localRemoteSplitState.writeDefaultValues = false;
            localRemoteSplitState.motion = optimizerState.oneFrameBuffer;
            localRemoteSplitState.hideFlags = HideFlags.HideInHierarchy;
            syncingLayer.stateMachine.defaultState = localRemoteSplitState;

            AnimatorStateMachine localStateMachine =
                syncingLayer.stateMachine.AddStateMachine("Local", position: new Vector3(100, 200, 0));
            localStateMachine.hideFlags = HideFlags.HideInHierarchy;

            AnimatorStateMachine remoteStateMachine = syncingLayer.stateMachine.AddStateMachine("Remote", position: new Vector3(-100, 200, 0));
            remoteStateMachine.hideFlags = HideFlags.HideInHierarchy;

            localStateMachine.anyStatePosition = new Vector3(20, 20, 0);
            localStateMachine.entryPosition = new Vector3(20, 50, 0);

            remoteStateMachine.anyStatePosition = new Vector3(20, 20, 0);
            remoteStateMachine.entryPosition = new Vector3(20, 50, 0);

            AnimatorStateTransition localTransition = localRemoteSplitState.AddTransition(localStateMachine);
            AnimatorStateTransition remoteTransition = localRemoteSplitState.AddTransition(remoteStateMachine);
            localTransition.AddCondition(AnimatorConditionMode.If, 0, "IsLocal");
            remoteTransition.AddCondition(AnimatorConditionMode.IfNot, 0, "IsLocal");
            optimizerState.localStateMachine = localStateMachine;
            optimizerState.remoteStateMachine = remoteStateMachine;
        }

        public static void UninstallMemOpt(VRCAvatarDescriptor avatar, AnimatorController fxLayer, VRCExpressionParameters expressionParameters)
        {
            List<VRCExpressionParameters.Parameter> generatedExpressionParams = new List<VRCExpressionParameters.Parameter>();
            List<VRCExpressionParameters.Parameter> optimizedParams = new List<VRCExpressionParameters.Parameter>();
            List<AnimatorControllerParameter> generatedAnimatorParams = new List<AnimatorControllerParameter>();
            foreach (AnimatorControllerParameter controllerParam in fxLayer.parameters)
            {
                if (controllerParam.name.Contains(prefix))
                {
                    generatedAnimatorParams.Add(controllerParam);
                }
            }

            List<AnimatorControllerLayer> mainBlendTreeLayers = fxLayer.FindHiddenIdentifier(mainBlendTreeIdentifier);
            List<AnimatorControllerLayer> syncingLayers = fxLayer.FindHiddenIdentifier(syncingLayerIdentifier);

            if (mainBlendTreeLayers.Count > 1)
            {
                Debug.LogError("Too many memory optimizer blendtrees found, unable to unintall automatically!");
                return;
            }
            if (syncingLayers.Count != 1)
            {
                Debug.LogError(syncingLayers.Count + " syncing layers found, unable to uninstall automatically");
                return;
            }

            List<ChildAnimatorState> states = syncingLayers[0].FindAllStatesInLayer();
            List<ChildAnimatorState> setStates = states.Where(x => x.state.name.Contains("Set Value ")).ToList();
            foreach (ChildAnimatorState state in setStates)
            {
                VRCAvatarParameterDriver paramdriver = (VRCAvatarParameterDriver)state.state.behaviours[0];
                List<VRC_AvatarParameterDriver.Parameter> paramdriverParams = paramdriver.parameters;
                foreach (VRC_AvatarParameterDriver.Parameter param in paramdriverParams)
                {
                    if (!String.IsNullOrEmpty(param.source))
                    {
                        foreach (VRCExpressionParameters.Parameter item in expressionParameters.parameters.Where(x => x.name == param.source))
                        {
                            optimizedParams.Add(item);
                        }
                    }
                }
            }

            foreach (VRCExpressionParameters.Parameter item in expressionParameters.parameters.Where(x => x.name.Contains(prefix)))
            {
                generatedExpressionParams.Add(item);
            }

            if (generatedExpressionParams.Count <= 0)
            {
                Debug.LogError("Too few generated expression parameters found! Only " + generatedExpressionParams.Count + " found. Aborting uninstall...");
                return;
            }
            if (generatedAnimatorParams.Count <= 0)
            {
                Debug.LogError("Too few generated animator parameters found! Only " + generatedAnimatorParams.Count + " found. Aborting uninstall...");
                return;
            }
            if (optimizedParams.Count < 2)
            {
                Debug.LogError("Too few optimized parameters found! Only " + optimizedParams.Count + " found. Aborting uninstall...");
                return;
            }

            foreach(AnimatorControllerLayer mainBlendTreeLayer in mainBlendTreeLayers)
            {
                Debug.Log("Animator layer " + mainBlendTreeLayer.name + " of index " + fxLayer.FindLayerIndex(mainBlendTreeLayer) + " is being deleted");
                fxLayer.RemoveLayer(mainBlendTreeLayer);
            }

            foreach (AnimatorControllerLayer syncingLayer in syncingLayers)
            {
                Debug.Log("Animator layer " + syncingLayer.name + " of index " + fxLayer.FindLayerIndex(syncingLayer) + " is being deleted");
                fxLayer.RemoveLayer(syncingLayer);
            }

            foreach (VRCExpressionParameters.Parameter param in generatedExpressionParams)
            {
                Debug.Log("Expression param " + param.name + "  of type: " + param.valueType + " is being deleted");
                expressionParameters.parameters = expressionParameters.parameters.Where(x => x != param).ToArray();
            }
            foreach (AnimatorControllerParameter param in generatedAnimatorParams)
            {
                Debug.Log("Controller param " + param.name + "  of type: " + param.type + " is being deleted");
                fxLayer.RemoveParameter(param);
            }
            foreach (var param in optimizedParams)
            {
                Debug.Log("Optimized param " + param.name + "  of type: " + param.valueType + " setting to sync");
                param.networkSynced = true;
            }



        }
    }
}