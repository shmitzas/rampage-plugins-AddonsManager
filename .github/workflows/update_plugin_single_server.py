
import argparse
import json
import os
import sys
import paramiko
import traceback

def main():
    parser = argparse.ArgumentParser(description="Deploy plugin to a single remote server")
    parser.add_argument('--plugin-name', required=True, help='Name of the plugin to be uploaded')
    parser.add_argument('--plugin-source', required=True, help='Directory containing source of plugin files to be uploaded')
    parser.add_argument('--plugin-destination', required=True, help='Directory where plugin files should be uploaded on the server')
    parser.add_argument('--server', required=True, help='Server name')
    parser.add_argument('--servers-config', required=True, help='JSON configuration containing server details')
    parser.add_argument('--password-prod', required=True, help='Password for production servers')
    parser.add_argument('--password-test', required=True, help='Password for test servers')
    args = parser.parse_args()

    plugin_source = args.plugin_source
    plugin_destination = args.plugin_destination
    plugin_name = args.plugin_name
    server = args.server

    if not os.path.isdir(plugin_source):
        print(f"Source directory not found: {plugin_source}")
        sys.exit(1)

    # Parse server configuration JSON
    try:
        servers_config = json.loads(args.servers_config)
    except json.JSONDecodeError as e:
        print(f"Error parsing servers configuration JSON: {e}")
        sys.exit(1)

    if server not in servers_config:
        print(f"Unknown server: {server}")
        print(f"Available servers: {', '.join(servers_config.keys())}")
        sys.exit(1)

    # Password map
    passwords = {
        "prod": args.password_prod,
        "test": args.password_test
    }

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

def upload_plugin(server, host, port, user, password, plugin_name, plugin_source, plugin_destination):
    print(f"Connecting to {server} ({host}:{port}) via SFTP...")
    
    # Upload files via SFTP
    try:
        # Create Transport object directly
        transport = paramiko.Transport((host, port))
        
        # Connect with username and password
        print(f"Connecting to {host}:{port} as {user}...")
        transport.connect(username=user, password=password)
        
        # Create SFTP client
        sftp = paramiko.SFTPClient.from_transport(transport)
        print(f"SFTP connection established successfully to {server}!")
        
        # Upload all files from plugin_source to plugin_destination
        upload_directory(sftp, plugin_source, plugin_destination)

        print(f"✅ \"{plugin_name}\" uploaded to {server} in \"{plugin_destination}\" successfully!")
        sftp.close()
        transport.close()
    except Exception as e:
        print(f"Error uploading \"{plugin_name}\" to {server}: {str(e)}")
        print(f"Host: {host}:{port}, User: {user}")
        # Print detailed traceback
        print("Detailed error traceback:")
        traceback.print_exc()
        
        # Try to get more information about the error
        if "Authentication" in str(e):
            print("\nAuthentication error details:")
            print("- Check if username and password are correct")
            print("- Verify that the server accepts password authentication")
            print("- Check if the account is locked or has access restrictions")
        elif "Connection" in str(e):
            print("\nConnection error details:")
            print("- Verify hostname and port are correct")
            print("- Check if there's a firewall blocking the connection")
            print("- Make sure the SFTP service is running on the server")
        
        sys.exit(1)

def upload_directory(sftp, local_dir, remote_dir):
    """Upload a directory to the remote server via SFTP"""
    # Create remote directory if it doesn't exist
    try:
        sftp.stat(remote_dir)
        print(f"Remote directory {remote_dir} already exists")
    except FileNotFoundError:
        print(f"Remote directory {remote_dir} not found, creating it...")
        sftp.mkdir(remote_dir)
        print(f"Created remote directory: {remote_dir}")
    
    # Upload all files in the directory with a proper buffer size
    for item in os.listdir(local_dir):
        local_path = os.path.join(local_dir, item)
        remote_path = f"{remote_dir}/{item}"
        
        if os.path.isfile(local_path):
            print(f"Uploading {local_path} to {remote_path}...")
            
            # Use a file object with a buffer instead of direct path
            with open(local_path, 'rb') as local_file:
                sftp.putfo(local_file, remote_path)
                
            # Verify file exists on remote server
            try:
                sftp.stat(remote_path)
                print(f"✓ Verified file exists: {remote_path}")
            except FileNotFoundError:
                print(f"WARNING: File upload may have failed - could not verify: {remote_path}")
                
        elif os.path.isdir(local_path):
            # Recursively upload subdirectories
            upload_directory(sftp, local_path, remote_path)

if __name__ == "__main__":
    main()
