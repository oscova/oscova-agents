# Create or edit task

Choose what an agent should do and when it should run.

## Define the work

- Enter **Name**, **Instruction**, and **Agent**.
- At least one agent must be available.
- Keep **Use agent settings**, or edit **Custom task settings** for this task.
- Choose a model override if needed.
- Background **Approval Mode** must automatically reject or approve required actions.

## Schedule and notify

- **Interval** requires at least one minute.
- **One-shot** and **Cron** use your current timezone.
- **After another task** requires a different existing task without a dependency cycle.
- **Wait while I'm using the provider** delays scheduled starts during your active provider requests.
- Run now, webhooks, and chained starts bypass that wait.
- **Notify on completion** adds a success inbox notice.
- Failures and skipped runs always create notices, except a Vector Indexing skip for a missing embedding provider.
- **Keep last runs** deletes older runs and their transcripts after each run finishes. Leave it empty to keep every run.
- To get a message from the agent, name the channel in **Instruction**, such as Telegram. The web app is the default.

## Save the task

- For webhooks, copy the complete endpoint and define required parameters.
- Parameter names start with a letter and use letters, digits, underscores, or hyphens, with no duplicates.
- Webhook-enabled one-shot tasks must leave **Delete task after run** off.
- Choose **Enabled**, then **Create** or **Save**.

> **Note:** Auto-approved host shell commands can change the server. Cancel discards this form's edits.

## Related

- [Agent Tasks](agent-tasks.md) - return to the parent screen's help.
