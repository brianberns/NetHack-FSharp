namespace NetHack.Agent

/// A note the agent uses to plan ahead.
type Note =
    {
        /// Note content.
        Text : string

        /// Note age.
        Age : int
    }

module Note =

    /// Creates a note.
    let create text =
        {
            Text = text
            Age = 0
        }
