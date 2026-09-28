import * as path from 'node:path';
import * as cdk from 'aws-cdk-lib';
import * as cloudwatch from 'aws-cdk-lib/aws-cloudwatch';
import * as dynamodb from 'aws-cdk-lib/aws-dynamodb';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as logs from 'aws-cdk-lib/aws-logs';
import * as s3 from 'aws-cdk-lib/aws-s3';
import * as s3deploy from 'aws-cdk-lib/aws-s3-deployment';
import { Construct } from 'constructs';

interface BenchmarkFunction {
  function: lambda.Function;
  alias: lambda.Alias;
}

export class BenchmarkStack extends cdk.Stack {
  public constructor(scope: Construct, id: string, props?: cdk.StackProps) {
    super(scope, id, props);

    const dynamoQueryClrCode = this.dotnetAsset('DynamoQuery/Clr/DynamoQueryClr.csproj');
    const dynamoQueryNativeAotCode = this.nativeAotAsset('DynamoQuery/NativeAot/DynamoQueryNativeAot.csproj');
    const imageProcessingClrCode = this.dotnetAsset('ImageProcessing/Clr/ImageProcessingClr.csproj');
    const imageProcessingNativeAotCode = this.nativeAotAsset('ImageProcessing/NativeAot/ImageProcessingNativeAot.csproj');
    const csvProcessingClrCode = this.dotnetAsset('CsvProcessing/Clr/CsvProcessingClr.csproj');
    const csvProcessingNativeAotCode = this.nativeAotAsset('CsvProcessing/NativeAot/CsvProcessingNativeAot.csproj');
    const geoSpatialLookupClrCode = this.dotnetAsset('GeoSpatialLookup/Clr/GeoSpatialLookupClr.csproj');
    const geoSpatialLookupNativeAotCode = this.nativeAotAsset(
      'GeoSpatialLookup/NativeAot/GeoSpatialLookupNativeAot.csproj',
      'GeoSpatialLookupNativeAot',
    );
    const metrics: cloudwatch.Metric[] = [];
    const dynamoTable = new dynamodb.Table(this, 'DynamoQueryTable', {
      billingMode: dynamodb.BillingMode.PAY_PER_REQUEST,
      partitionKey: { name: 'pk', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'sk', type: dynamodb.AttributeType.STRING },
      tableName: this.resourceName('dynamo-query'),
      removalPolicy: cdk.RemovalPolicy.DESTROY,
    });
    const imageSourceBucket = new s3.Bucket(this, 'ImageProcessingSourceBucket', {
      autoDeleteObjects: true,
      blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
      bucketName: this.resourceName('image-source') + `-${this.account}`,
      encryption: s3.BucketEncryption.S3_MANAGED,
      removalPolicy: cdk.RemovalPolicy.DESTROY,
    });
    new s3deploy.BucketDeployment(this, 'ImageProcessingSampleImage', {
      sources: [
        s3deploy.Source.asset(path.join(__dirname, '..', 'assets'), {
          exclude: ['models', 'locations'],
        }),
      ],
      destinationBucket: imageSourceBucket,
      prune: false,
    });
    const csvSourceBucket = new s3.Bucket(this, 'CsvProcessingSourceBucket', {
      autoDeleteObjects: true,
      blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
      bucketName: this.resourceName('csv-source') + `-${this.account}`,
      encryption: s3.BucketEncryption.S3_MANAGED,
      removalPolicy: cdk.RemovalPolicy.DESTROY,
    });
    const geoSpatialSourceBucket = new s3.Bucket(this, 'GeoSpatialSourceBucket', {
      autoDeleteObjects: true,
      blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
      bucketName: this.resourceName('geospatial-source') + `-${this.account}`,
      encryption: s3.BucketEncryption.S3_MANAGED,
      removalPolicy: cdk.RemovalPolicy.DESTROY,
    });
    const geoSpatialAssetPath = path.join(__dirname, '..', 'assets', 'locations');
    new s3deploy.BucketDeployment(this, 'GeoSpatialParquet', {
      sources: [s3deploy.Source.asset(geoSpatialAssetPath, { exclude: ['geonames*'] })],
      destinationBucket: geoSpatialSourceBucket,
      destinationKeyPrefix: 'italy',
      ephemeralStorageSize: cdk.Size.gibibytes(10),
      memoryLimit: 3008,
      prune: false,
    });
    const dynamoQueryClr = this.addBenchmarkFunction('DynamoQueryClr', dynamoQueryClrCode, {
      deploymentType: 'StandardCLR',
      example: 'dynamo-query',
      handler: 'DynamoQueryClr::Benchmark.DynamoQuery.Clr.DynamoQueryHandler::HandleAsync',
      memorySize: 512,
      architecture: lambda.Architecture.ARM_64,
      environment: { DynamoDbTableName: dynamoTable.tableName },
    });
    dynamoTable.grantReadData(dynamoQueryClr.function);
    metrics.push(this.metric(dynamoQueryClr.function, 'StandardCLR', 'dynamo-query', 'DurationMs'));
    this.outputAlias('DynamoQueryClr', dynamoQueryClr.alias);

    const dynamoQuerySnapStart = this.addBenchmarkFunction('DynamoQuerySnapStart', dynamoQueryClrCode, {
      deploymentType: 'SnapStart',
      example: 'dynamo-query',
      handler: 'DynamoQueryClr::Benchmark.DynamoQuery.Clr.DynamoQuerySnapStartHandler::HandleAsync',
      // Measured working set is ~82MB; a smaller GC heap means less to page in on restore.
      memorySize: 512,
      architecture: lambda.Architecture.ARM_64,
      environment: { DynamoDbTableName: dynamoTable.tableName },
      snapStart: true,
    });
    dynamoTable.grantReadData(dynamoQuerySnapStart.function);
    metrics.push(this.metric(dynamoQuerySnapStart.function, 'SnapStart', 'dynamo-query', 'ColdStartMs'));
    metrics.push(this.metric(dynamoQuerySnapStart.function, 'SnapStart', 'dynamo-query', 'DurationMs'));
    this.outputAlias('DynamoQuerySnapStart', dynamoQuerySnapStart.alias);

    const dynamoQueryNativeAot = this.addBenchmarkFunction('DynamoQueryNativeAot', dynamoQueryNativeAotCode, {
      deploymentType: 'NativeAOT',
      example: 'dynamo-query',
      handler: 'bootstrap',
      memorySize: 512,
      architecture: lambda.Architecture.ARM_64,
      environment: { DynamoDbTableName: dynamoTable.tableName },
    });
    dynamoTable.grantReadData(dynamoQueryNativeAot.function);
    metrics.push(this.metric(dynamoQueryNativeAot.function, 'NativeAOT', 'dynamo-query', 'DurationMs'));
    this.outputAlias('DynamoQueryNativeAot', dynamoQueryNativeAot.alias);

    const imageProcessingClr = this.addBenchmarkFunction('ImageProcessingClr', imageProcessingClrCode, {
      deploymentType: 'StandardCLR',
      example: 'image-processing',
      handler: 'ImageProcessingClr::Benchmark.ImageProcessing.Clr.ImageProcessingHandler::HandleAsync',
      memorySize: 512,
      architecture: lambda.Architecture.ARM_64,
    });
    imageSourceBucket.grantRead(imageProcessingClr.function);
    metrics.push(this.metric(imageProcessingClr.function, 'StandardCLR', 'image-processing', 'DurationMs'));
    this.outputAlias('ImageProcessingClr', imageProcessingClr.alias);

    const imageProcessingSnapStart = this.addBenchmarkFunction('ImageProcessingSnapStart', imageProcessingClrCode, {
      deploymentType: 'SnapStart',
      example: 'image-processing',
      handler: 'ImageProcessingClr::Benchmark.ImageProcessing.Clr.ImageProcessingSnapStartHandler::HandleAsync',
      // Measured working set is ~108MB; a smaller GC heap means less to page in on restore.
      memorySize: 512,
      architecture: lambda.Architecture.ARM_64,
      environment: { ImageSourceBucketName: imageSourceBucket.bucketName },
      snapStart: true,
    });
    imageSourceBucket.grantRead(imageProcessingSnapStart.function);
    metrics.push(this.metric(imageProcessingSnapStart.function, 'SnapStart', 'image-processing', 'ColdStartMs'));
    metrics.push(this.metric(imageProcessingSnapStart.function, 'SnapStart', 'image-processing', 'DurationMs'));
    this.outputAlias('ImageProcessingSnapStart', imageProcessingSnapStart.alias);

    const imageProcessingNativeAot = this.addBenchmarkFunction('ImageProcessingNativeAot', imageProcessingNativeAotCode, {
      deploymentType: 'NativeAOT',
      example: 'image-processing',
      handler: 'bootstrap',
      memorySize: 512,
      architecture: lambda.Architecture.ARM_64,
    });
    imageSourceBucket.grantRead(imageProcessingNativeAot.function);
    metrics.push(this.metric(imageProcessingNativeAot.function, 'NativeAOT', 'image-processing', 'DurationMs'));
    this.outputAlias('ImageProcessingNativeAot', imageProcessingNativeAot.alias);

    const csvProcessingClr = this.addBenchmarkFunction('CsvProcessingClr', csvProcessingClrCode, {
      deploymentType: 'StandardCLR',
      example: 'csv-processing',
      handler: 'CsvProcessingClr::Benchmark.CsvProcessing.Clr.CsvProcessingHandler::HandleAsync',
      memorySize: 1024,
    });
    csvSourceBucket.grantRead(csvProcessingClr.function);
    metrics.push(this.metric(csvProcessingClr.function, 'StandardCLR', 'csv-processing', 'DurationMs'));
    this.outputAlias('CsvProcessingClr', csvProcessingClr.alias);

    const csvProcessingSnapStart = this.addBenchmarkFunction('CsvProcessingSnapStart', csvProcessingClrCode, {
      deploymentType: 'SnapStart',
      example: 'csv-processing',
      handler: 'CsvProcessingClr::Benchmark.CsvProcessing.Clr.CsvProcessingSnapStartHandler::HandleAsync',
      memorySize: 1024,
      environment: { CsvSourceBucketName: csvSourceBucket.bucketName },
      snapStart: true,
    });
    csvSourceBucket.grantRead(csvProcessingSnapStart.function);
    metrics.push(this.metric(csvProcessingSnapStart.function, 'SnapStart', 'csv-processing', 'ColdStartMs'));
    metrics.push(this.metric(csvProcessingSnapStart.function, 'SnapStart', 'csv-processing', 'DurationMs'));
    this.outputAlias('CsvProcessingSnapStart', csvProcessingSnapStart.alias);

    const csvProcessingNativeAot = this.addBenchmarkFunction('CsvProcessingNativeAot', csvProcessingNativeAotCode, {
      deploymentType: 'NativeAOT',
      example: 'csv-processing',
      handler: 'bootstrap',
      memorySize: 1024,
      architecture: lambda.Architecture.ARM_64,
    });
    csvSourceBucket.grantRead(csvProcessingNativeAot.function);
    metrics.push(this.metric(csvProcessingNativeAot.function, 'NativeAOT', 'csv-processing', 'DurationMs'));
    this.outputAlias('CsvProcessingNativeAot', csvProcessingNativeAot.alias);

    const geoSpatialLookupClr = this.addBenchmarkFunction('GeoSpatialLookupClr', geoSpatialLookupClrCode, {
      deploymentType: 'StandardCLR',
      example: 'geospatial-lookup',
      handler: 'GeoSpatialLookupClr::Benchmark.GeoSpatialLookup.Clr.GeoSpatialLookupHandler::HandleAsync',
      memorySize: 4096,
      ephemeralStorageSizeMb: 1024,
      environment: { LocationBucket: geoSpatialSourceBucket.bucketName, LocationPrefix: 'italy' },
    });
    geoSpatialSourceBucket.grantRead(geoSpatialLookupClr.function);
    metrics.push(this.metric(geoSpatialLookupClr.function, 'StandardCLR', 'geospatial-lookup', 'DurationMs'));
    this.outputAlias('GeoSpatialLookupClr', geoSpatialLookupClr.alias);

    const geoSpatialLookupSnapStart = this.addBenchmarkFunction('GeoSpatialLookupSnapStart', geoSpatialLookupClrCode, {
      deploymentType: 'SnapStart',
      example: 'geospatial-lookup',
      handler: 'GeoSpatialLookupClr::Benchmark.GeoSpatialLookup.Clr.GeoSpatialLookupSnapStartHandler::HandleAsync',
      memorySize: 4096,
      ephemeralStorageSizeMb: 512,
      environment: { LocationBucket: geoSpatialSourceBucket.bucketName, LocationPrefix: 'italy' },
      snapStart: true,
    });
    geoSpatialSourceBucket.grantRead(geoSpatialLookupSnapStart.function);
    metrics.push(this.metric(geoSpatialLookupSnapStart.function, 'SnapStart', 'geospatial-lookup', 'ColdStartMs'));
    metrics.push(this.metric(geoSpatialLookupSnapStart.function, 'SnapStart', 'geospatial-lookup', 'DurationMs'));
    this.outputAlias('GeoSpatialLookupSnapStart', geoSpatialLookupSnapStart.alias);

    const geoSpatialLookupNativeAot = this.addBenchmarkFunction('GeoSpatialLookupNativeAot', geoSpatialLookupNativeAotCode, {
      deploymentType: 'NativeAOT',
      example: 'geospatial-lookup',
      handler: 'bootstrap',
      memorySize: 4096,
      ephemeralStorageSizeMb: 1024,
      architecture: lambda.Architecture.ARM_64,
      environment: { LocationBucket: geoSpatialSourceBucket.bucketName, LocationPrefix: 'italy' },
    });
    geoSpatialSourceBucket.grantRead(geoSpatialLookupNativeAot.function);
    metrics.push(this.metric(geoSpatialLookupNativeAot.function, 'NativeAOT', 'geospatial-lookup', 'DurationMs'));
    this.outputAlias('GeoSpatialLookupNativeAot', geoSpatialLookupNativeAot.alias);

    new cdk.CfnOutput(this, 'DynamoQueryTableName', {
      value: dynamoTable.tableName,
      description: 'DynamoDB table queried by the DynamoQuery benchmark.',
    });
    new cdk.CfnOutput(this, 'ImageProcessingSourceBucketName', {
      value: imageSourceBucket.bucketName,
      description: 'S3 bucket for source images used by the image-processing benchmark.',
    });
    new cdk.CfnOutput(this, 'CsvProcessingSourceBucketName', {
      value: csvSourceBucket.bucketName,
      description: 'S3 bucket for a 20 MB or larger CSV used by the csv-processing benchmark.',
    });
    new cdk.CfnOutput(this, 'GeoSpatialSourceBucketName', {
      value: geoSpatialSourceBucket.bucketName,
      description: "S3 bucket containing Italy's Who's On First Parquet file used by the geospatial benchmark.",
    });

    const dashboard = new cloudwatch.Dashboard(this, 'BenchmarkDashboard', {
      dashboardName: this.resourceName('metrics'),
    });
    dashboard.addWidgets(
      new cloudwatch.GraphWidget({
        title: 'Invocation duration',
        left: metrics.filter((metric) => metric.metricName === 'DurationMs'),
        leftAnnotations: [],
        statistic: 'p50',
        width: 12,
      }),
      new cloudwatch.GraphWidget({
        title: 'Cold start and memory signals',
        left: metrics.filter((metric) => metric.metricName !== 'DurationMs'),
        statistic: 'p50',
        width: 12,
      }),
    );
  }

  private dotnetAsset(projectFile: string): lambda.AssetCode {
    return lambda.Code.fromAsset(path.join(__dirname, '..', 'src'), {
      bundling: {
        image: lambda.Runtime.DOTNET_10.bundlingImage,
        // ReadyToRun cuts the JIT work paid during Init (and any lazily-JITted methods left
        // for first invoke), shrinking cold start for both StandardCLR and the pre-snapshot
        // init phase that SnapStart checkpoints from.
        command: ['bash', '-c', `dotnet publish ${projectFile} -c Release -p:PublishReadyToRun=true -o /asset-output`],
      },
    });
  }

  private nativeAotAsset(projectFile: string, executableName = path.basename(projectFile, '.csproj')): lambda.AssetCode {
    return lambda.Code.fromAsset(path.join(__dirname, '..', 'src'), {
      bundling: {
        image: cdk.DockerImage.fromRegistry('public.ecr.aws/sam/build-dotnet10:latest-arm64'),
        command: [
          'bash',
          '-c',
          `dotnet publish ${projectFile} -c Release -r linux-arm64 -p:PublishAot=true -p:StripSymbols=true -p:DebugType=None -o /asset-output && mv /asset-output/${executableName} /asset-output/bootstrap && find /asset-output -type f -name '*.dbg' -delete && find /asset-output -type f -name '*.pdb' -delete`,
        ],
      },
    });
  }

  private addBenchmarkFunction(
    id: string,
    code: lambda.AssetCode,
    options: {
      deploymentType: string;
      example: string;
      handler: string;
      memorySize: number;
      timeoutSeconds?: number;
      ephemeralStorageSizeMb?: number;
      architecture?: lambda.Architecture;
      environment?: Record<string, string>;
      snapStart?: boolean;
    },
  ): BenchmarkFunction {
    const fn = new lambda.Function(this, id, {
      functionName: this.resourceName(id),
      runtime: options.handler === 'bootstrap' ? lambda.Runtime.PROVIDED_AL2023 : lambda.Runtime.DOTNET_10,
      code,
      handler: options.handler,
      architecture: options.architecture ?? lambda.Architecture.ARM_64,
      memorySize: options.memorySize,
      timeout: cdk.Duration.seconds(options.timeoutSeconds ?? 30),
      ephemeralStorageSize: options.ephemeralStorageSizeMb
        ? cdk.Size.mebibytes(options.ephemeralStorageSizeMb)
        : undefined,
      environment: {
        BENCHMARK_DEPLOYMENT_TYPE: options.deploymentType,
        BENCHMARK_EXAMPLE: options.example,
        ...options.environment,
      },
      snapStart: options.snapStart ? lambda.SnapStartConf.ON_PUBLISHED_VERSIONS : undefined,
      logGroup: new logs.LogGroup(this, `${id}Logs`, {
        logGroupName: `/aws/lambda/${this.resourceName(id)}`,
        retention: logs.RetentionDays.ONE_WEEK,
        removalPolicy: cdk.RemovalPolicy.DESTROY,
      }),
      description: `${options.deploymentType} benchmark: ${options.example}`,
    });
    const alias = fn.currentVersion.addAlias(this.resourceName('live'));
    return { function: fn, alias };
  }

  private resourceName(name: string): string {
    const kebabName = name.replace(/([a-z0-9])([A-Z])/g, '$1-$2').toLowerCase();
    return `besharp-dotnet-lambda-${kebabName}`;
  }

  private requiredEnvironment(name: string): string {
    const value = process.env[name]?.trim();
    if (!value) {
      throw new Error(`Missing required environment variable: ${name}`);
    }

    return value;
  }

  private metric(
    fn: lambda.Function,
    deploymentType: string,
    example: string,
    metricName: string,
  ): cloudwatch.Metric {
    return new cloudwatch.Metric({
      namespace: 'DotnetLambdaBenchmarks',
      metricName,
      dimensionsMap: {
        FunctionName: fn.functionName,
        DeploymentType: deploymentType,
        Example: example,
      },
      period: cdk.Duration.minutes(1),
    });
  }

  private outputAlias(id: string, alias: lambda.Alias): void {
    new cdk.CfnOutput(this, `${id}AliasArn`, {
      value: alias.functionArn,
      description: `Invoke the ${id} benchmark through the live alias.`,
    });
  }
}
