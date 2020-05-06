﻿using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Media.Imaging;

namespace FileManager.Class
{
    /// <summary>
    /// 为图片查看提供支持
    /// </summary>
    public sealed class PhotoDisplaySupport : INotifyPropertyChanged
    {
        /// <summary>
        /// 获取Bitmap图片对象
        /// </summary>
        public BitmapImage BitmapSource { get; private set; }

        /// <summary>
        /// 获取Photo文件名称
        /// </summary>
        public string FileName
        {
            get
            {
                return PhotoFile.Name;
            }
        }

        /// <summary>
        /// 指示当前的显示是否是缩略图
        /// </summary>
        private bool IsThumbnailPicture = true;

        /// <summary>
        /// 旋转角度
        /// </summary>
        public int RotateAngle { get; set; } = 0;

        /// <summary>
        /// 获取Photo的StorageFile对象
        /// </summary>
        public FileSystemStorageItem PhotoFile { get; private set; }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// 初始化PhotoDisplaySupport的实例
        /// </summary>
        /// <param name="ImageSource">缩略图</param>
        /// <param name="File">文件</param>
        public PhotoDisplaySupport(FileSystemStorageItem Item)
        {
            PhotoFile = Item;
        }

        /// <summary>
        /// 使用原图替换缩略图
        /// </summary>
        /// <returns></returns>
        public async Task ReplaceThumbnailBitmapAsync()
        {
            if (IsThumbnailPicture)
            {
                IsThumbnailPicture = false;

                if ((await PhotoFile.GetStorageItem().ConfigureAwait(true)) is StorageFile File)
                {
                    using (IRandomAccessStream Stream = await File.OpenAsync(FileAccessMode.Read))
                    {
                        if (BitmapSource == null)
                        {
                            BitmapSource = new BitmapImage();
                        }

                        await BitmapSource.SetSourceAsync(Stream);
                    }

                    OnPropertyChanged(nameof(BitmapSource));
                }
            }
        }

        public async Task GenerateThumbnailAsync()
        {
            if (BitmapSource != null)
            {
                return;
            }

            if ((await PhotoFile.GetStorageItem().ConfigureAwait(true)) is StorageFile File)
            {
                using (StorageItemThumbnail ThumbnailStream = await File.GetThumbnailAsync(ThumbnailMode.PicturesView))
                {
                    if (BitmapSource == null)
                    {
                        BitmapSource = new BitmapImage();
                    }

                    await BitmapSource.SetSourceAsync(ThumbnailStream);
                }

                OnPropertyChanged(nameof(BitmapSource));
            }
        }

        /// <summary>
        /// 更新图片的显示
        /// </summary>
        /// <returns></returns>
        public async Task UpdateImage()
        {
            if ((await PhotoFile.GetStorageItem().ConfigureAwait(true)) is StorageFile File)
            {
                using (IRandomAccessStream Stream = await File.OpenAsync(FileAccessMode.Read))
                {
                    await BitmapSource.SetSourceAsync(Stream);
                }

                OnPropertyChanged(nameof(BitmapSource));
            }
        }

        /// <summary>
        /// 根据RotateAngle的值来旋转图片
        /// </summary>
        /// <returns></returns>
        public async Task<SoftwareBitmap> GenerateImageWithRotation()
        {
            if ((await PhotoFile.GetStorageItem().ConfigureAwait(true)) is StorageFile File)
            {
                using (IRandomAccessStream stream = await File.OpenAsync(FileAccessMode.Read))
                {
                    BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);

                    switch (RotateAngle % 360)
                    {
                        case 0:
                            {
                                return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                            }
                        case 90:
                            {
                                using (var Origin = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied))
                                {
                                    SoftwareBitmap Processed = new SoftwareBitmap(BitmapPixelFormat.Bgra8, Origin.PixelHeight, Origin.PixelWidth, BitmapAlphaMode.Premultiplied);
                                    OpenCV.OpenCVLibrary.RotateEffect(Origin, Processed, 90);
                                    return Processed;
                                }
                            }
                        case 180:
                            {
                                var Origin = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                                OpenCV.OpenCVLibrary.RotateEffect(Origin, Origin, 180);
                                return Origin;
                            }
                        case 270:
                            {
                                using (var Origin = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied))
                                {
                                    SoftwareBitmap Processed = new SoftwareBitmap(BitmapPixelFormat.Bgra8, Origin.PixelHeight, Origin.PixelWidth, BitmapAlphaMode.Premultiplied);
                                    OpenCV.OpenCVLibrary.RotateEffect(Origin, Processed, -90);
                                    return Processed;
                                }
                            }
                        default:
                            {
                                return null;
                            }
                    }
                }
            }
            else
            {
                return null;
            }
        }

        private void OnPropertyChanged(string Name)
        {
            if (!string.IsNullOrEmpty(Name))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Name));
            }
        }
    }
}
