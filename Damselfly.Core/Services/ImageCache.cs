using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Damselfly.Core.Models;
using Damselfly.Core.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Damselfly.Core.Services
{
    public class ImageCache
    {
        private readonly IMemoryCache _memoryCache;
        private readonly MemoryCacheEntryOptions _cacheOptions;

        public ImageCache(IMemoryCache memoryCache)
        {
            _memoryCache = memoryCache;
            _cacheOptions = new MemoryCacheEntryOptions()
                            .SetSlidingExpiration(TimeSpan.FromDays(2));
        }

        public async Task<Image> GetCachedImage(int imgId)
        {
            Image cachedImage;

            if (!_memoryCache.TryGetValue(imgId, out cachedImage))
            {
                Logging.LogVerbose($"Cache miss for image {imgId}");
                cachedImage = await EnrichAndCache(imageId: imgId);
            }

            return cachedImage;
        }

        public async Task<Image> GetCachedImage( Image img )
        {
            Image cachedImage;

            if( ! _memoryCache.TryGetValue(img.ImageId, out cachedImage) )
            {
                Logging.LogVerbose($"Cache miss for image {img.ImageId}");
                cachedImage = await EnrichAndCache(image: img);
            }

            return cachedImage;
        }

        public async Task<List<Image>> EnrichAndCache( List<Image> images )
        {
            var result = new List<Image>();

            foreach (var image in images)
            {
                result.Add( await GetCachedImage(image) );
            }

            return result;
        }

        private async Task<Image> EnrichAndCache(Image image = null, int imageId = 0)
        {
            var enrichedImage = await GetImage(image, imageId);

            if (enrichedImage != null)
            {
                _memoryCache.Set(enrichedImage.ImageId, enrichedImage, _cacheOptions);
            }

            return enrichedImage;
        }

        public void Evict( int imageId )
        {
            _memoryCache.Remove(imageId);
        }

        /// <summary>
        /// Get a single image and its metadata, ready to be cached.
        /// </summary>
        /// <param name="imageId"></param>
        /// <returns></returns>
        private static async Task<Image> GetImage(Image image, int imageId)
        {
            using var db = new ImageContext();
            var watch = new Stopwatch("EnrichForCache");
            var loadtype = "unknown";

            try
            {
                // We're either passed an existing image, or an image id.
                if (image != null)
                {
                    loadtype = "object";
                    var entry = db.Attach(image);

                    if (!entry.Reference(x => x.Folder).IsLoaded)
                        await entry.Reference(x => x.Folder)
                                .LoadAsync();

                    if (!entry.Reference(x => x.MetaData).IsLoaded)
                        await entry.Reference(x => x.MetaData)
                                   .Query()
                                   .Include(x => x.Camera)
                                   .Include(x => x.Lens)
                                   .LoadAsync();

                    if (!entry.Collection(x => x.BasketEntries).IsLoaded)
                        await entry.Collection(x => x.BasketEntries).LoadAsync();
                }
                else if (imageId != 0)
                {
                    loadtype = "id";
                    image = await db.Images
                                    .Where(x => x.ImageId == imageId)
                                    .Include(x => x.Folder)
                                    .Include(x => x.MetaData)
                                    .Include(x => x.MetaData.Camera)
                                    .Include(x => x.MetaData.Lens)
                                    .Include(x => x.BasketEntries)
                                    .FirstOrDefaultAsync();
                }
                else
                    throw new ArgumentException("Neither Image or ImageId were passed to GetImage");

                if (image != null)
                {
                    /// Because of this issue: https://github.com/dotnet/efcore/issues/19418
                    /// we have to explicitly load the tags, rather than using eager loading.

                    if (!db.Entry(image).Collection(e => e.ImageTags).IsLoaded)
                    {
                        // Now load the tags
                        await db.Entry(image).Collection(e => e.ImageTags)
                                    .Query()
                                    .Include(e => e.Tag)
                                    .LoadAsync();
                    }

                    if (!db.Entry(image).Collection(e => e.ImageObjects).IsLoaded)
                    {
                        await db.Entry(image).Collection(e => e.ImageObjects)
                                     .Query()
                                     .Include(x => x.Tag)
                                     .Include(x => x.Person)
                                     .LoadAsync();
                    }
                }
                else
                    throw new ArgumentException("Logic error.");
            }
            catch (Exception ex)
            {
                Logging.Log($"Exception retrieving image: {ex.Message}");
            }
            finally
            {
                watch.Stop();
                Logging.LogVerbose($"Cache enrich from {loadtype} took {watch.ElapsedTime}ms");
            }


            return image;
        }
    }
}

