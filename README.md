# SMS Redirect Cloud Function

A Google Cloud Function that receives a Twilio "message received" webhook, pulls a one-time code out of the SMS body (if present), and emails it to a predefined address via Gmail SMTP. If no code is found, it sends a generic email with the raw SMS body instead.

## Environment variables

All values are expected to be provided as secrets (e.g. via Google Secret Manager) bound to these environment variable names — never commit real values.

| Variable | Required | What goes in it |
|---|---|---|
| `TWILIO_AUTH_TOKEN` | Yes | Your Twilio account's Auth Token (Twilio Console → Account Info). Used to validate the `X-Twilio-Signature` header so only genuine Twilio requests are processed. |
| `GMAIL_ADDRESS` | Yes | The Gmail address the function sends from. |
| `GMAIL_APP_PASSWORD` | Yes | A 16-character Gmail App Password for `GMAIL_ADDRESS` (myaccount.google.com/apppasswords, requires 2-Step Verification), with spaces removed. |
| `RECIPIENT_EMAIL` | Yes | Default destination email address, used when `RECIPIENT_MAP` isn't set or doesn't match the receiving number. |
| `RECIPIENT_MAP` | No | JSON object mapping a receiving Twilio number (E.164 format, the SMS's `To` field) to a specific destination email, e.g. `{"+15551234567":"a@example.com","+15559876543":"b@example.com"}`. Numbers not listed fall back to `RECIPIENT_EMAIL`. |
