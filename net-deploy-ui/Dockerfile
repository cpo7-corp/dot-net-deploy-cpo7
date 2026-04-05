# Stage 1: Build Angular application
FROM node:22-alpine AS build
WORKDIR /app

# Copy package.json and lock file (if present)
COPY package*.json ./

# Install dependencies
RUN npm install --legacy-peer-deps

# Copy source code and build
COPY . .
RUN npm run build:docker

# Stage 2: Serve using Nginx
FROM nginx:alpine
WORKDIR /usr/share/nginx/html

# Clear existing Nginx default static files
RUN rm -rf ./*

# Copy built app from the build stage
# Note: Angular 17+ with the new application builder outputs to dist/[project-name]/browser
COPY --from=build /app/dist/net-deploy-ui/browser .

# Copy custom Nginx configuration
COPY nginx.conf /etc/nginx/conf.d/default.conf

EXPOSE 80

CMD ["nginx", "-g", "daemon off;"]
