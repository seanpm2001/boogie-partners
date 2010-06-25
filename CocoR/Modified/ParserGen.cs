/*-------------------------------------------------------------------------
ParserGen.cs -- Generation of the Recursive Descent Parser
Compiler Generator Coco/R,
Copyright (c) 1990, 2004 Hanspeter Moessenboeck, University of Linz
extended by M. Loeberbauer & A. Woess, Univ. of Linz
with improvements by Pat Terry, Rhodes University

This program is free software; you can redistribute it and/or modify it 
under the terms of the GNU General Public License as published by the 
Free Software Foundation; either version 2, or (at your option) any 
later version.

This program is distributed in the hope that it will be useful, but 
WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY 
or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License 
for more details.

You should have received a copy of the GNU General Public License along 
with this program; if not, write to the Free Software Foundation, Inc., 
59 Temple Place - Suite 330, Boston, MA 02111-1307, USA.

As an exception, it is allowed to write an extension of Coco/R that is
used as a plugin in non-free software.

If not otherwise stated, any source code generated by Coco/R (other than 
Coco/R itself) does not fall under the GNU General Public License.
-------------------------------------------------------------------------*/
using System;
using System.IO;
using System.Collections;
using System.Text;

namespace at.jku.ssw.Coco {

public class ParserGen {

	const int maxTerm = 3;		// sets of size < maxTerm are enumerated
	const char CR  = '\r';
	const char LF  = '\n';
	const int EOF = -1;

	const int tErr = 0;			// error codes
	const int altErr = 1;
	const int syncErr = 2;
	
	public Position usingPos; // "using" definitions from the attributed grammar
	
	int errorNr;      // highest parser error number
	Symbol curSy;     // symbol whose production is currently generated
	FileStream fram;  // parser frame file
	StreamWriter gen; // generated parser source file
	StringWriter err; // generated parser error messages
	ArrayList symSet = new ArrayList();
	
	Tab tab;          // other Coco objects
	TextWriter trace;
	Errors errors;
	Buffer buffer;
	
	void Indent (int n) {
		for (int i = 1; i <= n; i++) gen.Write('\t');
	}
	
	
	bool Overlaps(BitArray s1, BitArray s2) {
		int len = s1.Count;
		for (int i = 0; i < len; ++i) {
			if (s1[i] && s2[i]) {
				return true;
			}
		}
		return false;
	}
	
	// use a switch if more than 5 alternatives and none starts with a resolver
	bool UseSwitch (Node p) {
		BitArray s1, s2;
		if (p.typ != Node.alt) return false;
		int nAlts = 0;
		s1 = new BitArray(tab.terminals.Count);
		while (p != null) {
			s2 = tab.Expected0(p.sub, curSy);
			// must not optimize with switch statement, if there are ll1 warnings
			if (Overlaps(s1, s2)) { return false; }
			s1.Or(s2);
			++nAlts;
			// must not optimize with switch-statement, if alt uses a resolver expression
			if (p.sub.typ == Node.rslv) return false;
			p = p.down;
		}
		return nAlts > 5;
	}
	
	void CopyFramePart (string stop) {
		char startCh = stop[0];
		int endOfStopString = stop.Length-1;
		int ch = fram.ReadByte();
		while (ch != EOF)
			if (ch == startCh) {
				int i = 0;
				do {
					if (i == endOfStopString) return; // stop[0..i] found
					ch = fram.ReadByte(); i++;
				} while (ch == stop[i]);
				// stop[0..i-1] found; continue with last read character
				gen.Write(stop.Substring(0, i));
			} else {
				gen.Write((char)ch); ch = fram.ReadByte();
			}
		throw new FatalError("Incomplete or corrupt parser frame file");
	}

	void CopySourcePart (Position pos, int indent) {
		// Copy text described by pos from atg to gen
		int ch, i;
		if (pos != null) {
			buffer.Pos = pos.beg; ch = buffer.Read();
			Indent(indent);
			while (buffer.Pos <= pos.end) {
				while (ch == CR || ch == LF) {  // eol is either CR or CRLF or LF
					gen.WriteLine(); Indent(indent);
					if (ch == CR) ch = buffer.Read(); // skip CR
					if (ch == LF) ch = buffer.Read(); // skip LF
					for (i = 1; i <= pos.col && (ch == ' ' || ch == '\t'); i++) { 
						// skip blanks at beginning of line
						ch = buffer.Read();
					}
					if (i <= pos.col) pos.col = i - 1; // heading TABs => not enough blanks
					if (buffer.Pos > pos.end) goto done;
				}
				gen.Write((char)ch);
				ch = buffer.Read();
			}
			done:
			if (indent > 0) gen.WriteLine();
		}
	}

	void GenErrorMsg (int errTyp, Symbol sym) {
		errorNr++;
		err.Write("\t\t\tcase " + errorNr + ": s = \"");
		switch (errTyp) {
			case tErr: 
				if (sym.name[0] == '"') err.Write(tab.Escape(sym.name) + " expected");
				else err.Write(sym.name + " expected"); 
				break;
			case altErr: err.Write("invalid " + sym.name); break;
			case syncErr: err.Write("this symbol not expected in " + sym.name); break;
		}
		err.WriteLine("\"; break;");
	}
	
	int NewCondSet (BitArray s) {
		for (int i = 1; i < symSet.Count; i++) // skip symSet[0] (reserved for union of SYNC sets)
			if (Sets.Equals(s, (BitArray)symSet[i])) return i;
		symSet.Add(s.Clone());
		return symSet.Count - 1;
	}
	
	void GenCond (BitArray s, Node p) {
		if (p.typ == Node.rslv) CopySourcePart(p.pos, 0);
		else {
			int n = Sets.Elements(s);
			if (n == 0) gen.Write("false"); // happens if an ANY set matches no symbol
			else if (n <= maxTerm)
				foreach (Symbol sym in tab.terminals) {
					if (s[sym.n]) {
						gen.Write("la.kind == {0}", sym.n);
						--n;
						if (n > 0) gen.Write(" || ");
					}
				}
			else
				gen.Write("StartOf({0})", NewCondSet(s));
		}
	}
		
	void PutCaseLabels (BitArray s) {
		foreach (Symbol sym in tab.terminals)
			if (s[sym.n]) gen.Write("case {0}: ", sym.n);
	}
	
	void GenCode (Node p, int indent, BitArray isChecked) {
		Node p2;
		BitArray s1, s2;
		while (p != null) {
			switch (p.typ) {
				case Node.nt: {
					Indent(indent);
					gen.Write(p.sym.name + "(");
					CopySourcePart(p.pos, 0);
					gen.WriteLine(");");
					break;
				}
				case Node.t: {
					Indent(indent);
					// assert: if isChecked[p.sym.n] is true, then isChecked contains only p.sym.n
					if (isChecked[p.sym.n]) gen.WriteLine("Get();");
					else gen.WriteLine("Expect({0});", p.sym.n);
					break;
				}
				case Node.wt: {
					Indent(indent);
					s1 = tab.Expected(p.next, curSy);
					s1.Or(tab.allSyncSets);
					gen.WriteLine("ExpectWeak({0}, {1});", p.sym.n, NewCondSet(s1));
					break;
				}
				case Node.any: {
					Indent(indent);
					int acc = Sets.Elements(p.set);
					if (tab.terminals.Count == (acc + 1) || (acc > 0 && Sets.Equals(p.set, isChecked))) {
						// either this ANY accepts any terminal (the + 1 = end of file), or exactly what's allowed here
						gen.WriteLine("Get();");
					} else {
						GenErrorMsg(altErr, curSy);
						if (acc > 0) {
							gen.Write("if ("); GenCond(p.set, p); gen.WriteLine(") Get(); else SynErr({0});", errorNr);
						} else gen.WriteLine("SynErr({0}); // ANY node that matches no symbol", errorNr);
					}
					break;
				}
				case Node.eps: break; // nothing
				case Node.rslv: break; // nothing
				case Node.sem: {
					CopySourcePart(p.pos, indent);
					break;
				}
				case Node.sync: {
					Indent(indent);
					GenErrorMsg(syncErr, curSy);
					s1 = (BitArray)p.set.Clone();
					gen.Write("while (!("); GenCond(s1, p); gen.Write(")) {");
					gen.Write("SynErr({0}); Get();", errorNr); gen.WriteLine("}");
					break;
				}
				case Node.alt: {
					s1 = tab.First(p);
					bool equal = Sets.Equals(s1, isChecked);
					bool useSwitch = UseSwitch(p);
					if (useSwitch) { Indent(indent); gen.WriteLine("switch (la.kind) {"); }
					p2 = p;
					while (p2 != null) {
						s1 = tab.Expected(p2.sub, curSy);
						Indent(indent);
						if (useSwitch) { 
							PutCaseLabels(s1); gen.WriteLine("{");
						} else if (p2 == p) { 
							gen.Write("if ("); GenCond(s1, p2.sub); gen.WriteLine(") {"); 
						} else if (p2.down == null && equal) { gen.WriteLine("} else {");
						} else { 
							gen.Write("} else if (");  GenCond(s1, p2.sub); gen.WriteLine(") {"); 
						}
						GenCode(p2.sub, indent + 1, s1);
						if (useSwitch) {
							Indent(indent); gen.WriteLine("\tbreak;");
							Indent(indent); gen.WriteLine("}");
						}
						p2 = p2.down;
					}
					Indent(indent);
					if (equal) {
						gen.WriteLine("}");
					} else {
						GenErrorMsg(altErr, curSy);
						if (useSwitch) {
							gen.WriteLine("default: SynErr({0}); break;", errorNr);
							Indent(indent); gen.WriteLine("}");
						} else {
							gen.Write("} "); gen.WriteLine("else SynErr({0});", errorNr);
						}
					}
					break;
				}
				case Node.iter: {
					Indent(indent);
					p2 = p.sub;
					gen.Write("while (");
					if (p2.typ == Node.wt) {
						s1 = tab.Expected(p2.next, curSy);
						s2 = tab.Expected(p.next, curSy);
						gen.Write("WeakSeparator({0},{1},{2}) ", p2.sym.n, NewCondSet(s1), NewCondSet(s2));
						s1 = new BitArray(tab.terminals.Count);  // for inner structure
						if (p2.up || p2.next == null) p2 = null; else p2 = p2.next;
					} else {
						s1 = tab.First(p2);
						GenCond(s1, p2);
					}
					gen.WriteLine(") {");
					GenCode(p2, indent + 1, s1);
					Indent(indent); gen.WriteLine("}");
					break;
				}
				case Node.opt:
					s1 = tab.First(p.sub);
					Indent(indent);
					gen.Write("if ("); GenCond(s1, p.sub); gen.WriteLine(") {");
					GenCode(p.sub, indent + 1, s1);
					Indent(indent); gen.WriteLine("}");
					break;
			}
			if (p.typ != Node.eps && p.typ != Node.sem && p.typ != Node.sync) 
				isChecked.SetAll(false);  // = new BitArray(tab.terminals.Count);
			if (p.up) break;
			p = p.next;
		}
	}
	
	void GenTokens() {
		foreach (Symbol sym in tab.terminals) {
			if (Char.IsLetter(sym.name[0]))
				gen.WriteLine("\tpublic const int _{0} = {1};", sym.name, sym.n);
		}
	}
	
	void GenPragmas() {
		foreach (Symbol sym in tab.pragmas) {
			gen.WriteLine("\tpublic const int _{0} = {1};", sym.name, sym.n);
		}
	}

	void GenCodePragmas() {
		foreach (Symbol sym in tab.pragmas) {
			gen.WriteLine("\t\t\t\tif (la.kind == {0}) {{", sym.n);
			CopySourcePart(sym.semPos, 4);
			gen.WriteLine("\t\t\t\t}");
		}
	}

	void GenProductions() {
		foreach (Symbol sym in tab.nonterminals) {
			curSy = sym;
			gen.Write("\tvoid {0}(", sym.name);
			CopySourcePart(sym.attrPos, 0);
			gen.WriteLine(") {");
			CopySourcePart(sym.semPos, 2);
			GenCode(sym.graph, 2, new BitArray(tab.terminals.Count));
			gen.WriteLine("\t}"); gen.WriteLine();
		}
	}
	
	void InitSets() {
		for (int i = 0; i < symSet.Count; i++) {
			BitArray s = (BitArray)symSet[i];
			gen.Write("\t\t{");
			int j = 0;
			foreach (Symbol sym in tab.terminals) {
				if (s[sym.n]) gen.Write("T,"); else gen.Write("x,");
				++j;
				if (j%4 == 0) gen.Write(" ");
			}
			if (i == symSet.Count-1) gen.WriteLine("x}"); else gen.WriteLine("x},");
		}
	}
	
	void OpenGen(bool backUp) { /* pdt */
		try {
			string fn = Path.Combine(tab.outDir, "Parser.cs"); /* pdt */
			if (File.Exists(fn) && backUp) File.Copy(fn, fn + ".old", true);
			gen = new StreamWriter(new FileStream(fn, FileMode.Create)); /* pdt */
		} catch (IOException) {
			throw new FatalError("Cannot generate parser file");
		}
	}

	public void WriteParser () {
		int oldPos = buffer.Pos;  // Pos is modified by CopySourcePart
		symSet.Add(tab.allSyncSets);
		string fr = Path.Combine(tab.srcDir, "Parser.frame");
		if (!File.Exists(fr)) {
			if (tab.frameDir != null) fr = Path.Combine(tab.frameDir.Trim(), "Parser.frame");
			if (!File.Exists(fr)) throw new FatalError("Cannot find Parser.frame");
		}
		try {
			fram = new FileStream(fr, FileMode.Open, FileAccess.Read, FileShare.Read);
		} catch (IOException) {
			throw new FatalError("Cannot open Parser.frame.");
		}
		OpenGen(true); /* pdt */
		err = new StringWriter();
		foreach (Symbol sym in tab.terminals) GenErrorMsg(tErr, sym);
		
		CopyFramePart("-->begin");
		if (!tab.srcName.ToLower().EndsWith("coco.atg")) {
			gen.Close(); OpenGen(false); /* pdt */
		}
		if (usingPos != null) {CopySourcePart(usingPos, 0); gen.WriteLine();}
		CopyFramePart("-->namespace");
		/* AW open namespace, if it exists */
		if (tab.nsName != null && tab.nsName.Length > 0) {
			gen.WriteLine("namespace {0} {{", tab.nsName);
			gen.WriteLine();
		}
		CopyFramePart("-->constants");
		GenTokens(); /* ML 2002/09/07 write the token kinds */
		gen.WriteLine("\tpublic const int maxT = {0};", tab.terminals.Count-1);
		GenPragmas(); /* ML 2005/09/23 write the pragma kinds */
		CopyFramePart("-->declarations"); CopySourcePart(tab.semDeclPos, 0);
		CopyFramePart("-->pragmas"); GenCodePragmas();
		CopyFramePart("-->productions"); GenProductions();
		CopyFramePart("-->parseRoot"); gen.WriteLine("\t\t{0}();", tab.gramSy.name);
		CopyFramePart("-->initialization"); InitSets();
		CopyFramePart("-->errors"); gen.Write(err.ToString());
		CopyFramePart("$$$");
		/* AW 2002-12-20 close namespace, if it exists */
		if (tab.nsName != null && tab.nsName.Length > 0) gen.Write("}");
		gen.Close();
		buffer.Pos = oldPos;
	}
	
	public void WriteStatistics () {
		trace.WriteLine();
		trace.WriteLine("{0} terminals", tab.terminals.Count);
		trace.WriteLine("{0} symbols", tab.terminals.Count + tab.pragmas.Count +
		                               tab.nonterminals.Count);
		trace.WriteLine("{0} nodes", tab.nodes.Count);
		trace.WriteLine("{0} sets", symSet.Count);
	}

	public ParserGen (Parser parser) {
		tab = parser.tab;
		errors = parser.errors;
		trace = parser.trace;
		buffer = parser.scanner.buffer;
		errorNr = -1;
		usingPos = null;
	}

} // end ParserGen

} // end namespace
