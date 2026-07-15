namespace NetHack.Agent

open System
open System.IO

open NetHack.Api

module View =

    let private truncateRuler len (chunks : seq<string>) : string =
        chunks
            |> Seq.collect id
            |> Seq.truncate len
            |> Seq.toArray
            |> String

    /// Creates a view of the given state and the action to be
    /// taken in that state.
    let createView state (notes : _[]) (aa : AgentAction) =

        use wtr = new StringWriter()

            // messages that led to current state
        wtr.WriteLine()
        for msg in state.Observation.Messages do
            wtr.WriteLine($"{msg}")

            // dungeon map
        let rulerTens =
            Seq.initInfinite (fun i -> $"{i}         ")
                |> truncateRuler state.Observation.Width
        let rulerUnits =
            Seq.initInfinite (fun _ -> "0123456789")
                |> truncateRuler state.Observation.Width
        wtr.WriteLine()
        wtr.WriteLine($"  {rulerTens}")
        wtr.WriteLine($"  {rulerUnits}")
        for (i, row) in Seq.indexed state.Observation.Rows do
            let toChar n = char n + '0'
            let cTens = if i % 10 = 0 then toChar (i / 10) else ' '
            let cUnits = toChar (i % 10)
            wtr.WriteLine($"{cTens}{cUnits}{row}{cUnits}{cTens}")
        wtr.WriteLine($"  {rulerUnits}")
        wtr.WriteLine($"  {rulerTens}")

            // hero status
        let status = state.Observation.Status
        wtr.WriteLine()
        wtr.WriteLine($"{status.Title} \
            St:{status.Strength} \
            Dx:{status.Dexterity} \
            Co:{status.Constitution} \
            In:{status.Intelligence} \
            Wi:{status.Wisdom} \
            Ch:{status.Charisma} \
            {status.Alignment}")
        wtr.WriteLine($"Dlvl:{status.DungeonLevel} \
            $:{status.Gold} \
            HP:{status.HP}/{status.HPMax} \
            Pw:{status.Power}/{status.PowerMax} \
            AC:{status.ArmorClass} \
            T:{status.Turns}")

            // what the game is waiting for
        wtr.WriteLine()
        match state.Pending with
            | Menu (title, mode, items) ->
                wtr.WriteLine($"Pending: Menu [{title}] {mode}")
                for item in items do
                    wtr.WriteLine($"   {item.Key} - {item.Text}")
            | pending ->
                wtr.WriteLine($"Pending: {pending}")

            // notes
        if notes.Length > 0 then
            wtr.WriteLine()
            wtr.WriteLine("Existing notes:")
            for i = 0 to notes.Length - 1 do
                let note = notes[i]
                wtr.WriteLine($"   {i+1}({note.Age}): {note.Text}")
        if aa.NotesToAdd.Length > 0 then
            wtr.WriteLine()
            wtr.WriteLine("Notes to add:")
            for note in aa.NotesToAdd do
                wtr.WriteLine($"   {note}")
        if aa.NotesToDelete.Length > 0 then
            wtr.WriteLine()
            wtr.WriteLine($"Notes to delete: %A{aa.NotesToDelete}")
        if aa.RelevantNotes.Length > 0 then
            wtr.WriteLine()
            wtr.WriteLine($"Relevant notes: %A{aa.RelevantNotes}")

            // action to take in the given state
        wtr.WriteLine()
        wtr.WriteLine($"Action: {Prompt.getActionDesc aa}")

            // expected result of action
        wtr.WriteLine()
        wtr.WriteLine($"Prediction: {aa.Prediction}")

            // divider
        wtr.WriteLine()
        wtr.WriteLine(String('-', 64))

        wtr.ToString()

    /// Renders a view of the given state.
    let render state notes aa =
        let view = createView state notes aa
        Console.Write(view)

        do
            use wtr =
                new StreamWriter(
                    $"Agent{state.GameId}.log",
                    append = true)
            fprintf wtr "%s" view

        Console.WriteLine("Press enter to continue")
        Console.ReadLine() |> ignore
