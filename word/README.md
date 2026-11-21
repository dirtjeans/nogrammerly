# Nogrammerly for Word (experimental)

A Word add-in that checks your document with the Nogrammerly app on the same PC. It underlines issues in the document and lists each one, with its fix, in a side pane. Nothing you write leaves the PC.

The add-in does no checking itself. It sends each paragraph to the Nogrammerly app over a local connection, called [the bridge](../windows/README.md#add-ins-for-other-apps-the-bridge). The app runs the same spelling, style and on-device AI checks it uses everywhere else.

## What you need

- Nogrammerly for Windows, installed and running.
- A recent Word for Windows. It must support the WordApi 1.6 requirement set.

Underlines in the document need Word for Microsoft 365. Without it, the side pane still lists every issue and can fix each one.

## Set up

1. Close Word if it is open.
2. Click the ✦ icon near the clock, then choose **Add-ins for other apps › Set up Word add-in…**.
3. Confirm the certificate when Windows asks.

Word then opens with Nogrammerly loaded. From then on, **Nogrammerly** is on Word's **Home** tab. The Nogrammerly app serves the add-in's pages to Word itself, so nothing else needs to run.

If the button ever goes missing, choose **Add-ins for other apps › Open Word with Nogrammerly**. You can also find it in Word under **Home › Add-ins › Developer Add-ins**.

Install Nogrammerly with `Install.cmd` before you set up the add-in. The installer is what tells Word where the add-in lives.

### Why a certificate?

Word only loads add-ins from a secure (HTTPS) address. So the app makes a certificate for the address `localhost` and asks Windows to trust it. The certificate is for your user account on this PC only. It can't be used for any other site, and it can't sign other certificates. Its private key stays in your Windows key store.

To undo all of this, choose **Add-ins for other apps › Remove Word add-in**. Uninstalling Nogrammerly does the same.

## Run it from source instead

Developers can serve the add-in's files with Node.js 20 or later, without the app's help.

1. Turn on the bridge. Click the ✦ icon, then choose **Add-ins for other apps › Let add-ins on this PC use Nogrammerly**.
2. Make a local certificate. Windows asks you to confirm it.

   ```powershell
   npm run certs
   ```

3. Serve the add-in. Leave this running while you use Word.

   ```powershell
   npm start
   ```

4. Add it to Word. This opens Word with the add-in loaded.

   ```powershell
   npm run sideload
   ```

5. On Word's **Home** tab, choose **Nogrammerly**. In the ✦ menu, choose **Add-ins for other apps › Copy add-in key**, and paste the key into the pane.

To remove this copy from Word, run `npm run unload`.

## How to use it

- Issues are underlined as you type: red for correctness, blue for clarity, purple for delivery and pink for style.
- Click an underline to see the fix, then accept or dismiss it.
- The side pane lists every issue. **Fix** applies it, **Show** selects it in the document, and **Ignore** hides it for good, in every app.
- To rewrite text, select it, pick a tone and choose **Rewrite selection**. You see the result before anything changes.

## Limits

- Only the main body is checked. Headers, footers, footnotes and text boxes are not.
- Word's pop-up shows a fix only when there is text to offer. Issues with no fix, or whose fix is to delete a word, are handled in the side pane.
- A paragraph that holds a picture, a field or a footnote mark may show an underline a little out of place.
- When you run it from source, the key is kept in the add-in's browser storage on this PC.

## Hosting the files somewhere else

`manifest.xml` points at `https://localhost:3000`. To serve `src` and `assets` from another HTTPS address, make a manifest for it:

```powershell
npm run hosted -- https://example.github.io/nogrammerly
```

This writes `manifest.hosted.xml`. Then add the site, such as `https://example.github.io`, to `"origins"` in `%LOCALAPPDATA%\Nogrammerly\bridge.json` and restart Nogrammerly. The app refuses web pages that are not on that list. Load the new manifest with `npm run sideload:hosted`.

This route is not proven. Some browsers stop a page on the public web from calling a program on your PC, and Word may do the same. If the pane says it can't reach Nogrammerly, go back to the local server.

## Develop

```powershell
npm test
```

| File | What's in it |
|---|---|
| `src/core.js` | Talking to the bridge and turning its issues into Word underlines. No Word code, so `npm test` covers it. |
| `src/taskpane.js` | The pane: reads paragraphs from Word, applies fixes and keeps up with edits. |
| `manifest.xml` | Tells Word where the add-in lives and names the strings used in underline pop-ups. |
| `serve.js` | Serves `src` and `assets` on `https://localhost:3000`, to this PC only. |
