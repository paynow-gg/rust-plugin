# PayNow Plugin for Rust / Hurtworld / 7 Days To Die

The official PayNow addon seamlessly integrates with game servers for Rust, Hurtworld, and 7 Days to Die, facilitating in-game transactions and enhancing gameplay with secure and efficient financial interactions. This guide will help you set up the PayNow plugin on your server for any of these supported games.

## Prerequisites

Ensure you have administrative access to your Rust server and the ability to modify its configuration.

## Installation

The PayNow plugin configuration is automatically saved into the `oxide/config/paynow.json` directory upon the plugin's first load on your server. No manual file transfer is required for installation.

## Configuration

After the plugin is automatically installed and configured for the first time, you may want to customize its settings, such as the server connection token and the command fetch interval.

### Setting Your Token

To connect your server with the PayNow gameserver, set your unique PayNow token using the following server console command:

```plaintext
paynow.token <token>
```

Replace `<token>` with your actual PayNow token.

### Adjusting Fetch Interval

The default fetch interval is recommended for most servers, but you can adjust it to meet your specific needs by modifying the config.

## Support

For support, questions, or more information, join our Discord community:

- [Discord](https://discord.gg/paynow)

## Contributing

Contributions are welcome! If you'd like to improve the PayNow plugin or suggest new features, please fork the repository, make your changes, and submit a pull request.
