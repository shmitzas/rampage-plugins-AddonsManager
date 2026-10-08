
import argparse
import json
import sys
from update_plugin_single_server import upload_plugin

def main():
    parser = argparse.ArgumentParser(description="Deploy plugin to multiple remote servers")
    parser.add_argument('--servers', required=True, help='Comma-separated list of server names')
    parser.add_argument('--plugin-name', required=True, help='Name of the plugin to be uploaded')
    parser.add_argument('--plugin-source', required=True, help='Directory containing source of plugin files to be uploaded')
    parser.add_argument('--plugin-destination', required=True, help='Directory where plugin files should be uploaded on the server')
    parser.add_argument('--servers-config', required=True, help='JSON configuration containing server details')
    parser.add_argument('--password-prod', required=True, help='Password for production servers')
    parser.add_argument('--password-test', required=True, help='Password for test servers')
    args = parser.parse_args()

    servers = [s.strip() for s in args.servers.split(",") if s.strip()]
    plugin_source = args.plugin_source
    plugin_destination = args.plugin_destination
    plugin_name = args.plugin_name

    # Parse server configuration JSON
    try:
        servers_config = json.loads(args.servers_config)
    except json.JSONDecodeError as e:
        print(f"Error parsing servers configuration JSON: {e}")
        sys.exit(1)

    # Password map
    passwords = {
        "prod": args.password_prod,
        "test": args.password_test
    }

    for server in servers:
        if server not in servers_config:
            print(f"Unknown server: {server}")
            print(f"Available servers: {', '.join(servers_config.keys())}")
            sys.exit(1)
        
        config = servers_config[server]
        host = config["host"]
        port = config["port"]
        user = config["username"]
        password_type = config.get("password_type", "prod")
        password = passwords.get(password_type)
        
        if not password:
            print(f"Invalid password type '{password_type}' for server {server}")
            sys.exit(1)

        upload_plugin(server, host, port, user, password, plugin_name, plugin_source, plugin_destination)

if __name__ == "__main__":
    main()
