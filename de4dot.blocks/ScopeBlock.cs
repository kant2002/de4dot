/*
    Copyright (C) 2011-2015 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using dnlib.DotNet.Emit;

namespace de4dot.blocks {
	// A normal branch may not transfer out of a protected block (try block), filter handler,
	// an exception handler block, or a method.
	public abstract class ScopeBlock : BaseBlock {
		protected List<BaseBlock>? baseBlocks;

		public List<BaseBlock>? BaseBlocks {
			get => baseBlocks;
			set => baseBlocks = value;
		}

		public IEnumerable<BaseBlock> GetBaseBlocks() {
			if (baseBlocks != null) {
				foreach (var bb in baseBlocks)
					yield return bb;
			}
		}

		public List<BaseBlock> GetAllBaseBlocks() => GetTheBlocks(new List<BaseBlock>());
		public List<Block> GetAllBlocks() => GetTheBlocks(new List<Block>());

		public List<Block> GetAllBlocks(List<Block> allBlocks) {
			allBlocks.Clear();
			return GetTheBlocks(allBlocks);
		}

		public List<ScopeBlock> GetAllScopeBlocks() => GetTheBlocks(new List<ScopeBlock>());

		/// <summary>
		///     The first <see cref="Block"/> in this scope, i.e. the one control enters it at, or null
		///     if the scope holds no blocks at all.
		/// </summary>
		public Block? GetFirstBlock() {
			foreach (var bb in GetBaseBlocks()) {
				if (bb is Block block)
					return block;
				if (bb is ScopeBlock scopeBlock) {
					var first = scopeBlock.GetFirstBlock();
					if (first != null)
						return first;
				}
			}
			return null;
		}

		/// <summary>
		///     Adds the entry block of every exception handler protecting <paramref name="baseBlock"/>
		///     to <paramref name="entryBlocks"/>, walking outwards through nested try blocks.
		///
		///     A reachability walk that only follows <c>Targets</c>/<c>FallThrough</c> never enters a
		///     handler: a <see cref="TryBlock"/> keeps its handlers in
		///     <see cref="TryBlock.TryHandlerBlocks"/>, which is not part of the
		///     <see cref="BaseBlocks"/> the ordinary traversal descends into, and no branch may
		///     transfer into a handler either. So a handler is reachable exactly when its protected
		///     region is, and this is how such a walk discovers that.
		/// </summary>
		public static void AddProtectingHandlerEntryBlocks(BaseBlock? baseBlock, List<Block> entryBlocks) {
			for (var parent = baseBlock?.Parent; parent != null; parent = parent.Parent) {
				if (parent is not TryBlock tryBlock)
					continue;
				foreach (var handler in tryBlock.TryHandlerBlocks) {
					var filterEntry = handler.FilterHandlerBlock.GetFirstBlock();
					if (filterEntry != null)
						entryBlocks.Add(filterEntry);
					var handlerEntry = handler.HandlerBlock.GetFirstBlock();
					if (handlerEntry != null)
						entryBlocks.Add(handlerEntry);
				}
			}
		}

		public List<T> GetTheBlocks<T>(List<T> list) where T : BaseBlock {
			AddBlocks(list, this);
			return list;
		}

		void AddBlocks<T>(IList<T> list, ScopeBlock scopeBlock) where T : BaseBlock {
			foreach (var bb in scopeBlock.GetBaseBlocks()) {
				if (bb is T t)
					list.Add(t);
				if (bb is ScopeBlock)
					AddBlocks(list, (ScopeBlock)bb);
			}
		}

		List<Block> FindBlocks() => FindBlocks(null);

		List<Block> FindBlocks(Func<Block, bool>? blockChecker) {
			var blocks = new List<Block>();
			foreach (var bb in GetBaseBlocks()) {
				if (bb is Block block && (blockChecker == null || blockChecker(block)))
					blocks.Add(block);
			}
			return blocks;
		}

		internal bool GetLdcValue(Instr instr, out int value) {
			if (Code.Ldc_I4_0 <= instr.OpCode.Code && instr.OpCode.Code <= Code.Ldc_I4_8)
				value = instr.OpCode.Code - Code.Ldc_I4_0;
			else if (instr.OpCode.Code == Code.Ldc_I4)
				value = (int)instr.Operand;
			else if (instr.OpCode.Code == Code.Ldc_I4_S)
				value = (sbyte)instr.Operand;
			else if (instr.OpCode.Code == Code.Ldc_I4_M1)
				value = -1;
			else {
				value = 0;
				return false;
			}
			return true;
		}

		// Remove the block if it's a dead block. If it has refs to other dead blocks, those
		// are also removed.
		public void RemoveDeadBlock(Block block) => RemoveDeadBlocks(new List<Block> { block });

		// Remove all dead blocks we can find
		public void RemoveDeadBlocks() => RemoveDeadBlocks(FindBlocks());

		// Remove the blocks if they're dead blocks. If they have refs to other dead blocks,
		// those are also removed.
		public void RemoveDeadBlocks(List<Block> blocks) {
			Debug.Assert(baseBlocks != null);
			while (blocks.Count != 0) {
				var block = blocks[blocks.Count - 1];
				blocks.RemoveAt(blocks.Count - 1);
				if (block.Sources.Count != 0)
					continue;	// Not dead
				if (block == baseBlocks[0])
					continue;	// It's the start of this block fence so must be present
				if (!IsOurBaseBlock(block))
					continue;	// Some other ScopeBlock owns it, eg. first instr of an exception handler

				// It's a dead block we can delete!

				if (block.FallThrough != null)
					blocks.Add(block.FallThrough);
				if (block.Targets != null)
					blocks.AddRange(block.Targets);
				block.RemoveDeadBlock();
				if (!baseBlocks.Remove(block))
					throw new ApplicationException("Could not remove dead block from baseBlocks");
			}
		}

		public bool IsOurBaseBlock(BaseBlock? bb) => bb != null && bb.Parent == this;

		// For each block, if it has only one target, and the target has only one source, then
		// merge them into one block.
		public int MergeBlocks() {
			Debug.Assert(baseBlocks != null);
			int mergedBlocks = 0;
			var blocks = FindBlocks();
			for (int i = 0; i < blocks.Count; i++) {
				var block = blocks[i];
				var target = block.GetOnlyTarget();
				if (!IsOurBaseBlock(target))
					continue;	// Only merge blocks we own!
				if (!block.CanMerge(target))
					continue;	// Can't merge them!
				if (target == baseBlocks[0])
					continue;	// The first one has an implicit source (eg. start of method or exception handler)

				var targetIndex = blocks.IndexOf(target);
				if (targetIndex < 0)
					throw new ApplicationException("Could not remove target block from blocks");
				blocks.RemoveAt(targetIndex);
				block.Merge(target);
				if (!baseBlocks.Remove(target))
					throw new ApplicationException("Could not remove merged block from baseBlocks");
				if (targetIndex < i)
					i--;
				i--;				// Redo since there may be more blocks we can merge
				mergedBlocks++;
			}

			return mergedBlocks;
		}

		// If bb is in baseBlocks (a direct child), return bb. If bb is a BaseBlock in a
		// ScopeBlock that is a direct child, then return that ScopeBlock. Else return null.
		public BaseBlock? ToChild(BaseBlock? bb) {
			Debug.Assert(bb != null);
			if (IsOurBaseBlock(bb))
				return bb;

			for (var sb = bb.Parent; sb != null; sb = sb.Parent) {
				if (IsOurBaseBlock(sb))
					return sb;
			}

			return null;
		}

		internal void RepartitionBlocks() {
			Debug.Assert(baseBlocks != null);
			var newBaseBlocks = new BlocksSorter(this).Sort();

			const bool insane = true;
			if (insane) {
				if (newBaseBlocks.Count != baseBlocks.Count)
					throw new ApplicationException("BlocksSorter included too many/few BaseBlocks");
				if (baseBlocks.Count > 0 && baseBlocks[0] != newBaseBlocks[0])
					throw new ApplicationException("BlocksSorter removed the start block");
				foreach (var bb in baseBlocks) {
					if (!newBaseBlocks.Contains(bb))
						throw new ApplicationException("BlocksSorter forgot a child");
				}
			}

			baseBlocks = newBaseBlocks;
		}

		// Removes the TryBlock and all its TryHandlerBlocks. The code inside the try block
		// is not removed.
		public void RemoveTryBlock(TryBlock tryBlock) {
			Debug.Assert(baseBlocks != null);
			int tryBlockIndex = baseBlocks.IndexOf(tryBlock);
			if (tryBlockIndex < 0)
				throw new ApplicationException("Can't remove the TryBlock since it's not this ScopeBlock's TryBlock");

			Debug.Assert(tryBlock.BaseBlocks != null);
			foreach (var bb in tryBlock.BaseBlocks)
				bb.Parent = this;
			baseBlocks.RemoveAt(tryBlockIndex);
			baseBlocks.InsertRange(tryBlockIndex, tryBlock.BaseBlocks);
			tryBlock.BaseBlocks.Clear();

			// Get removed blocks and make sure they're not referenced by remaining code
			var removedBlocks = new List<Block>();
			foreach (var handler in tryBlock.TryHandlerBlocks)
				handler.GetTheBlocks(removedBlocks);
			if (!VerifyNoExternalRefs(removedBlocks))
				throw new ApplicationException("Removed blocks are referenced by remaining code");

			RemoveAllDeadBlocks(Utils.Convert<TryHandlerBlock, BaseBlock>(tryBlock.TryHandlerBlocks));
		}

		// Returns true if no external blocks references the blocks
		static bool VerifyNoExternalRefs(IList<Block> removedBlocks) {
			var removedDict = new Dictionary<Block, bool>();
			foreach (var removedBlock in removedBlocks)
				removedDict[removedBlock] = true;
			foreach (var removedBlock in removedBlocks) {
				foreach (var source in removedBlock.Sources) {
					if (!removedDict.TryGetValue(source, out bool val))
						return false;   // external code references a removed block
				}
			}
			return true;
		}

		// Remove all blocks in deadBlocks. They're guaranteed to be dead.
		void RemoveAllDeadBlocks(IEnumerable<BaseBlock> deadBlocks) => RemoveAllDeadBlocks(deadBlocks, null);

		// Remove all blocks in deadBlocks. They're guaranteed to be dead. deadBlocksDict is
		// a dictionary of all dead blocks (even those not in this ScopeBlock).
		internal void RemoveAllDeadBlocks(IEnumerable<BaseBlock> deadBlocks, Dictionary<BaseBlock, bool>? deadBlocksDict) {
			Debug.Assert(baseBlocks != null);

			// Verify that all the blocks really are dead. If all their source blocks are
			// dead, then they are dead.

			var allDeadBlocks = new List<Block>();
			foreach (var bb in deadBlocks) {
				if (bb is Block block)
					allDeadBlocks.Add(block);
				else if (bb is ScopeBlock sb)
					allDeadBlocks.AddRange(sb.GetAllBlocks());
				else
					throw new ApplicationException($"Unknown BaseBlock type {bb.GetType()}");
			}

			if (deadBlocksDict != null) {
				foreach (var block in allDeadBlocks) {
					if (block.Sources == null)
						continue;
					foreach (var source in block.Sources) {
						if (!deadBlocksDict.ContainsKey(source))
							throw new ApplicationException("Trying to remove a block that is not dead!");
					}
				}
			}

			foreach (var block in allDeadBlocks)
				block.RemoveGuaranteedDeadBlock();
			foreach (var bb in deadBlocks) {
				if (!baseBlocks.Remove(bb))
					throw new ApplicationException("Could not remove dead base block from baseBlocks");
			}
		}

		public void RemoveGuaranteedDeadBlock(Block block) {
			Debug.Assert(baseBlocks != null);
			if (!baseBlocks.Remove(block))
				throw new ApplicationException("Could not remove dead block");
			block.RemoveGuaranteedDeadBlock();
		}

		public void Add(Block block) {
			Debug.Assert(baseBlocks != null);
			if (block.Parent != null)
				throw new ApplicationException("Block already has a parent");
			baseBlocks.Add(block);
			block.Parent = this;
		}
	}
}
