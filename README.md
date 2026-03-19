# NET Deploy CPO7

**Automated Deployment & Management System for .NET Applications**

NET Deploy is a powerful tool designed to automate the process of pulling, building, and deploying .NET services (WebAPI, MVC, Workers), Angular, and React applications to remote VPS or local servers.

![Image 1](1.png)
![Image 2](2.png)

---

**© 2026 CPO7 - PROPRIETARY SOFTWARE. Free for learning, paid for commercial use.**

## Key Features
- **Git Integration**: Pull and build from any Git repository.
- **Multi-Service Deploy**: Deploy multiple services simultaneously.
- **Framework Support**: Detailed support for .NET (WebAPI, Workers), Angular, React, and Node.js.
- **IIS & Windows Services Support**: Automatic management of IIS sites and Windows Services.
- **Live Terminal Logs**: Real-time feedback during the deployment process.
- **Heartbeat Monitoring**: Automatic health checks after deployment.
- **Environment Variables Support**: Manage and update application settings during deployment.
- **Batch Operations**: Prepare all repositories before starting transfers.

## Getting Started

### 1. Server Configuration
- Ensure the API server (`net-deploy-api`) is running.
- Configure your Git settings and VPS environments in the settings panel.

### 2. UI Access
- Run the Angular UI on port `5432`.
- Configure your services with their Repo URL and target paths.

### 3. Deploy
- Select the services you want to deploy.
- Choose the target environment.
- Click **Deploy Selected** to start the automated process.

## System Requirements

> ⚠️ All of the following must be installed on the **server machine** that runs the API.

- **[Git](https://git-scm.com/download/win)** *(Required)* — Used to clone and pull repositories. Must be installed on the server.
- **.NET 10.0+ Runtime** — Required to run the API server. [Download](https://dotnet.microsoft.com/download)
- **[MongoDB](https://www.mongodb.com/try/download/community)** *(Required)* — Used to store system configurations and deployment logs.
- **Node.js + npm** *(Optional)* — Required only if deploying Angular/React projects. [Download](https://nodejs.org)
- **SSH/SFTP access** *(Optional)* — Required only when deploying to a remote VPS.

---

## Disclaimer & Warranty

**THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND**, EXPRESS OR IMPLIED. IN NO EVENT SHALL CPO7 BE LIABLE FOR ANY CLAIM, DAMAGES, OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

**Use of this software is at your own risk.** CPO7 is not responsible for any damage, data loss, or system failure caused by the software.

---

**© 2026 CPO7 - PROPRIETARY SOFTWARE. Free for learning, paid for commercial use.**
